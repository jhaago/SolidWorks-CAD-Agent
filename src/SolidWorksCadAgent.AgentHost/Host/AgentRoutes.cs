using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Jobs;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public sealed class AgentRoutes
    {
        private readonly SqliteJobRepository _repository;
        private readonly ISolidWorksSession _solidWorks;
        private readonly Func<DateTime> _utcNow;
        private readonly JobStateMachine _stateMachine;
        private readonly JobCoordinator _coordinator;
        private readonly AgentSettingsService _settingsService;

        public AgentRoutes(SqliteJobRepository repository, ISolidWorksSession solidWorks)
            : this(repository, solidWorks, null, null, () => DateTime.UtcNow)
        {
        }

        public AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            JobCoordinator coordinator)
            : this(repository, solidWorks, coordinator, null, () => DateTime.UtcNow)
        {
        }

        public AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            JobCoordinator coordinator,
            AgentSettingsService settingsService)
            : this(repository, solidWorks, coordinator, settingsService, () => DateTime.UtcNow)
        {
        }

        internal AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            Func<DateTime> utcNow)
            : this(repository, solidWorks, null, null, utcNow)
        {
        }

        internal AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            JobCoordinator coordinator,
            Func<DateTime> utcNow)
            : this(repository, solidWorks, coordinator, null, utcNow)
        {
        }

        internal AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            JobCoordinator coordinator,
            AgentSettingsService settingsService,
            Func<DateTime> utcNow)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _solidWorks = solidWorks ?? throw new ArgumentNullException(nameof(solidWorks));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _stateMachine = new JobStateMachine(_utcNow);
            _coordinator = coordinator;
            _settingsService = settingsService;
        }

        public async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();

            var method = (request.Method ?? string.Empty).ToUpperInvariant();
            var path = NormalizePath(request.Path);

            if (method == "GET" && path == "/health")
            {
                return Json(200, new { status = "ok", schemaVersion = 1 });
            }

            if (method == "GET" && path == "/solidworks/status")
            {
                return SolidWorksStatus(await _solidWorks.GetStatusAsync(cancellationToken).ConfigureAwait(false));
            }

            if (method == "POST" && path == "/solidworks/attach")
            {
                return SolidWorksStatus(await _solidWorks.AttachAsync(cancellationToken).ConfigureAwait(false));
            }

            if (method == "POST" && path == "/solidworks/launch")
            {
                return SolidWorksStatus(await _solidWorks.LaunchAsync(cancellationToken).ConfigureAwait(false));
            }

            if (path == "/settings")
            {
                if (method == "GET") return GetSettings();
                if (method == "PUT") return await UpdateSettingsAsync(request.Body, cancellationToken).ConfigureAwait(false);
            }

            if (path == "/credentials/openai")
            {
                if (method == "GET") return CredentialStatus();
                if (method == "PUT") return SetCredential(request.Body);
                if (method == "DELETE") return DeleteCredential();
            }

            if (method == "POST" && path == "/jobs")
            {
                return await CreateJobAsync(request.Body, cancellationToken).ConfigureAwait(false);
            }

            if (method == "GET" && path == "/jobs")
            {
                return await ListJobsAsync(request.Path, cancellationToken).ConfigureAwait(false);
            }

            if (TryParseJobPath(path, out var jobId, out var action))
            {
                if (method == "GET" && action == null)
                {
                    return await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                }

                if (method == "POST" && action == "cancel")
                {
                    return await TransitionJobAsync(jobId, JobState.Cancelled, cancellationToken).ConfigureAwait(false);
                }

                if (method == "POST" && action == "approve")
                {
                    return await ApproveJobAsync(jobId, request.Body, cancellationToken).ConfigureAwait(false);
                }

                if (method == "POST" && action == "request-changes")
                {
                    return await RequestChangesAsync(jobId, request.Body, cancellationToken).ConfigureAwait(false);
                }
            }

            return Error(404, "ROUTE_NOT_FOUND", "The requested Agent Host route does not exist.");
        }

        private async Task<AgentResponse> GetJobAsync(Guid id, CancellationToken cancellationToken)
        {
            var snapshot = await _repository.GetSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
            if (snapshot == null) return JobNotFound();
            try
            {
                return SnapshotResponse(200, snapshot);
            }
            catch (JsonException)
            {
                return Error(500, "CORRUPT_JOB_SNAPSHOT", "The persisted CAD job contains invalid JSON.");
            }
        }

        private AgentResponse GetSettings()
        {
            if (_settingsService == null)
                return Error(409, "SETTINGS_NOT_CONFIGURED", "Settings controls are not configured for this Host.");
            return Json(200, new { settings = SettingsProjection(_settingsService.Current) });
        }

        private async Task<AgentResponse> UpdateSettingsAsync(string body, CancellationToken cancellationToken)
        {
            if (_settingsService == null)
                return Error(409, "SETTINGS_NOT_CONFIGURED", "Settings controls are not configured for this Host.");
            try
            {
                var candidate = JsonConvert.DeserializeObject<AgentSettings>(body ?? string.Empty);
                if (candidate == null) return Error(400, "INVALID_SETTINGS", "A settings object is required.");
                var result = await _settingsService.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
                return Json(200, new
                {
                    settings = SettingsProjection(result.Settings),
                    restartRequired = result.RestartRequired
                });
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
            }
            catch (ArgumentException ex)
            {
                return Error(400, "INVALID_SETTINGS", ex.Message);
            }
            catch (AgentSettingsServiceException ex)
            {
                return Error(409, ex.Code, ex.Message);
            }
        }

        private AgentResponse CredentialStatus()
        {
            if (_settingsService == null)
                return Error(409, "SETTINGS_NOT_CONFIGURED", "Credential controls are not configured for this Host.");
            return Json(200, new { configured = _settingsService.IsOpenAiCredentialConfigured() });
        }

        private AgentResponse SetCredential(string body)
        {
            if (_settingsService == null)
                return Error(409, "SETTINGS_NOT_CONFIGURED", "Credential controls are not configured for this Host.");
            try
            {
                var request = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var apiKey = (string)request["apiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    return Error(400, "API_KEY_REQUIRED", "A non-empty OpenAI API key is required.");
                _settingsService.SetOpenAiCredential(apiKey);
                return Json(200, new { configured = true });
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
            }
        }

        private AgentResponse DeleteCredential()
        {
            if (_settingsService == null)
                return Error(409, "SETTINGS_NOT_CONFIGURED", "Credential controls are not configured for this Host.");
            _settingsService.DeleteOpenAiCredential();
            return Json(200, new { configured = false });
        }

        private static object SettingsProjection(AgentSettings settings)
        {
            return new
            {
                workspaceRoot = settings.WorkspaceRoot,
                autoMode = settings.AutoMode,
                openAiModel = settings.OpenAiModel,
                solidWorksExecutablePath = settings.SolidWorksExecutablePath,
                loggingLevel = settings.LoggingLevel,
                executionMode = settings.ExecutionMode.ToString()
            };
        }

        private async Task<AgentResponse> ListJobsAsync(string rawPath, CancellationToken cancellationToken)
        {
            int limit;
            string cursor;
            try
            {
                var limitText = GetQueryValue(rawPath, "limit");
                if (string.IsNullOrWhiteSpace(limitText))
                {
                    limit = 25;
                }
                else if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
                {
                    return Error(400, "INVALID_LIMIT", "The job page limit must be a positive integer.");
                }
                limit = Math.Min(limit, 100);
                cursor = GetQueryValue(rawPath, "cursor");
            }
            catch (UriFormatException)
            {
                return Error(400, "INVALID_CURSOR", "The job cursor is invalid.");
            }

            try
            {
                var page = await _repository.ListAsync(limit, cursor, cancellationToken).ConfigureAwait(false);
                return Json(200, new
                {
                    items = page.Items.Select(item => new
                    {
                        id = item.Id,
                        prompt = item.Prompt,
                        state = item.State.ToString(),
                        isSimulated = item.IsSimulated,
                        createdUtc = item.CreatedUtc,
                        updatedUtc = item.UpdatedUtc
                    }),
                    nextCursor = page.NextCursor
                });
            }
            catch (ArgumentException)
            {
                return Error(400, "INVALID_CURSOR", "The job cursor is invalid.");
            }
        }

        private async Task<AgentResponse> ApproveJobAsync(Guid id, string body, CancellationToken cancellationToken)
        {
            if (_coordinator != null)
            {
                JObject request;
                try
                {
                    request = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                }
                catch (JsonException)
                {
                    return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
                }

                if (!Guid.TryParse((string)request["revisionId"], out var revisionId))
                    return Error(400, "REVISION_REQUIRED", "Approval requires the current revisionId.");

                try
                {
                    var snapshot = await _coordinator.ApproveAndExecuteAsync(id, revisionId, cancellationToken)
                        .ConfigureAwait(false);
                    return SnapshotResponse(200, snapshot);
                }
                catch (JobCoordinatorException ex)
                {
                    return Error(ex.Code == "JOB_NOT_FOUND" ? 404 : 409, ex.Code, ex.Message);
                }
            }

            var job = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job == null) return JobNotFound();
            if (job.State != JobState.AwaitingApproval || !job.PlanValidated)
            {
                return Error(409, "INVALID_JOB_STATE", "Only a job with a validated plan awaiting approval can be approved.");
            }

            return await TransitionAndSaveAsync(job, JobState.Approved, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentResponse> RequestChangesAsync(Guid id, string body, CancellationToken cancellationToken)
        {
            if (_coordinator == null)
                return Error(409, "PLANNING_NOT_CONFIGURED", "Request Changes requires a configured planning coordinator.");

            JObject request;
            try
            {
                request = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
            }

            if (!Guid.TryParse((string)request["revisionId"], out var revisionId))
                return Error(400, "REVISION_REQUIRED", "Request Changes requires the current revisionId.");
            var instructions = (string)request["instructions"];
            if (string.IsNullOrWhiteSpace(instructions))
                return Error(400, "INSTRUCTIONS_REQUIRED", "Change instructions are required.");

            try
            {
                var snapshot = await _coordinator.RequestChangesAsync(id, revisionId, instructions, cancellationToken)
                    .ConfigureAwait(false);
                return SnapshotResponse(200, snapshot);
            }
            catch (JobCoordinatorException ex)
            {
                return Error(ex.Code == "JOB_NOT_FOUND" ? 404 : 409, ex.Code, ex.Message);
            }
        }

        private async Task<AgentResponse> TransitionJobAsync(
            Guid id,
            JobState nextState,
            CancellationToken cancellationToken)
        {
            var job = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job == null) return JobNotFound();
            return await TransitionAndSaveAsync(job, nextState, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentResponse> TransitionAndSaveAsync(
            CadJob job,
            JobState nextState,
            CancellationToken cancellationToken)
        {
            var expectedState = job.State;
            try
            {
                _stateMachine.Transition(job, nextState);
            }
            catch (JobStateTransitionException ex)
            {
                return Error(409, "INVALID_JOB_STATE", ex.Message);
            }

            var updated = await _repository.TryUpdateFromStateAsync(job, expectedState, cancellationToken).ConfigureAwait(false);
            if (!updated)
            {
                return Error(409, "CONCURRENT_JOB_UPDATE", "The CAD job changed while this request was being processed. Refresh and try again.");
            }
            return JobResponse(200, job);
        }

        private async Task<AgentResponse> CreateJobAsync(string body, CancellationToken cancellationToken)
        {
            CreateJobRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<CreateJobRequest>(body ?? string.Empty);
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_JSON", "The request body is not valid JSON.");
            }

            if (string.IsNullOrWhiteSpace(request?.Prompt))
            {
                return Error(400, "PROMPT_REQUIRED", "A non-empty CAD prompt is required.");
            }

            if (_coordinator != null)
            {
                try
                {
                    var snapshot = await _coordinator.CreateAndPlanAsync(request.Prompt, cancellationToken)
                        .ConfigureAwait(false);
                    return SnapshotResponse(201, snapshot);
                }
                catch (JobCoordinatorException ex)
                {
                    return Error(ex.Code == "PROMPT_REQUIRED" ? 400 : 409, ex.Code, ex.Message);
                }
            }

            var now = _utcNow();
            var job = new CadJob
            {
                Id = Guid.NewGuid(),
                Prompt = request.Prompt.Trim(),
                State = JobState.New,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            await _repository.CreateAsync(job, cancellationToken).ConfigureAwait(false);
            return Json(201, new { id = job.Id, state = job.State.ToString() });
        }

        private static AgentResponse SolidWorksStatus(SolidWorksSessionStatus status)
        {
            var runtime = status?.RuntimeInfo;
            return Json(200, new
            {
                isConnected = status?.IsConnected ?? false,
                isRunning = status?.IsRunning ?? false,
                isVisible = status?.IsVisible ?? false,
                runtime = runtime == null ? null : new
                {
                    revisionNumber = runtime.RevisionNumber,
                    releaseYear = runtime.ReleaseYear,
                    servicePack = runtime.ServicePack,
                    servicePackHotfix = runtime.ServicePackHotfix,
                    displayVersion = runtime.DisplayVersion
                },
                errorMessage = status?.ErrorMessage
            });
        }

        private static AgentResponse JobResponse(int statusCode, CadJob job)
        {
            return Json(statusCode, new
            {
                id = job.Id,
                prompt = job.Prompt,
                state = job.State.ToString(),
                planValidated = job.PlanValidated,
                hasUnresolvedAmbiguity = job.HasUnresolvedAmbiguity,
                ambiguityMessage = job.AmbiguityMessage,
                overwriteRequested = job.OverwriteRequested,
                overwriteAuthorized = job.OverwriteAuthorized,
                isSimulated = job.IsSimulated,
                outputPath = job.OutputPath,
                createdUtc = job.CreatedUtc,
                updatedUtc = job.UpdatedUtc
            });
        }

        private static AgentResponse SnapshotResponse(int statusCode, JobSnapshot snapshot)
        {
            var revision = snapshot?.Revisions?.OrderBy(item => item.RevisionNumber).LastOrDefault();
            var job = snapshot?.Job;
            if (job == null) return JobNotFound();
            return Json(statusCode, new
            {
                id = job.Id,
                prompt = job.Prompt,
                state = job.State.ToString(),
                planValidated = job.PlanValidated,
                hasUnresolvedAmbiguity = job.HasUnresolvedAmbiguity,
                ambiguityMessage = job.AmbiguityMessage,
                overwriteRequested = job.OverwriteRequested,
                overwriteAuthorized = job.OverwriteAuthorized,
                isSimulated = job.IsSimulated,
                outputPath = job.OutputPath,
                createdUtc = job.CreatedUtc,
                updatedUtc = job.UpdatedUtc,
                currentRevisionId = revision?.Id,
                currentRevisionNumber = revision?.RevisionNumber,
                plan = revision == null ? null : ParseOptionalJson(revision.PlanJson),
                commandCount = snapshot.Commands?.Count ?? 0,
                revisions = (snapshot.Revisions ?? new JobRevision[0]).Select(item => new
                {
                    id = item.Id,
                    jobId = item.JobId,
                    revisionNumber = item.RevisionNumber,
                    prompt = item.Prompt,
                    interpretation = ParseOptionalJson(item.InterpretationJson),
                    plan = ParseOptionalJson(item.PlanJson),
                    createdUtc = item.CreatedUtc
                }),
                commands = (snapshot.Commands ?? new CommandExecutionRecord[0]).Select(item => new
                {
                    id = item.Id,
                    jobId = item.JobId,
                    revisionNumber = item.RevisionNumber,
                    sequenceNumber = item.SequenceNumber,
                    commandName = item.CommandName,
                    parameters = ParseOptionalJson(item.ParametersJson),
                    success = item.Success,
                    result = ParseOptionalJson(item.ResultJson),
                    errorCode = item.ErrorCode,
                    errorMessage = item.ErrorMessage,
                    startedUtc = item.StartedUtc,
                    completedUtc = item.CompletedUtc
                }),
                verifications = (snapshot.Verifications ?? new VerificationResultRecord[0]).Select(item => new
                {
                    id = item.Id,
                    jobId = item.JobId,
                    revisionNumber = item.RevisionNumber,
                    checkName = item.CheckName,
                    passed = item.Passed,
                    expected = ParseOptionalJson(item.ExpectedJson),
                    actual = ParseOptionalJson(item.ActualJson),
                    createdUtc = item.CreatedUtc
                }),
                attachments = (snapshot.Attachments ?? new AttachmentRecord[0]).Select(item => new
                {
                    id = item.Id,
                    jobId = item.JobId,
                    revisionNumber = item.RevisionNumber,
                    kind = item.Kind,
                    path = item.Path,
                    createdUtc = item.CreatedUtc
                })
            });
        }

        private static JToken ParseOptionalJson(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : JToken.Parse(value);
        }

        private static AgentResponse JobNotFound()
        {
            return Error(404, "JOB_NOT_FOUND", "The requested CAD job does not exist.");
        }

        private static AgentResponse Json(int statusCode, object value)
        {
            return new AgentResponse(statusCode, JsonConvert.SerializeObject(value));
        }

        private static AgentResponse Error(int statusCode, string code, string message)
        {
            return Json(statusCode, new { error = new { code, message } });
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/";
            var queryIndex = path.IndexOf('?');
            var normalized = queryIndex >= 0 ? path.Substring(0, queryIndex) : path;
            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }

        private static string GetQueryValue(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var queryIndex = path.IndexOf('?');
            if (queryIndex < 0 || queryIndex == path.Length - 1) return null;

            foreach (var pair in path.Substring(queryIndex + 1).Split('&'))
            {
                var separator = pair.IndexOf('=');
                var rawKey = separator < 0 ? pair : pair.Substring(0, separator);
                var rawValue = separator < 0 ? string.Empty : pair.Substring(separator + 1);
                var key = Uri.UnescapeDataString(rawKey.Replace("+", " "));
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(rawValue.Replace("+", " "));
            }
            return null;
        }

        private static bool TryParseJobPath(string path, out Guid id, out string action)
        {
            id = Guid.Empty;
            action = null;
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || segments.Length > 3 || segments[0] != "jobs" || !Guid.TryParse(segments[1], out id))
            {
                return false;
            }

            action = segments.Length == 3 ? segments[2].ToLowerInvariant() : null;
            return true;
        }
    }
}
