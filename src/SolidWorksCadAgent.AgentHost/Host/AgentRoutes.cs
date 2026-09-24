using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;
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

        public AgentRoutes(SqliteJobRepository repository, ISolidWorksSession solidWorks)
            : this(repository, solidWorks, () => DateTime.UtcNow)
        {
        }

        internal AgentRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            Func<DateTime> utcNow)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _solidWorks = solidWorks ?? throw new ArgumentNullException(nameof(solidWorks));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _stateMachine = new JobStateMachine(_utcNow);
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

            if (method == "POST" && path == "/jobs")
            {
                return await CreateJobAsync(request.Body, cancellationToken).ConfigureAwait(false);
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
                    return await ApproveJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
            }

            return Error(404, "ROUTE_NOT_FOUND", "The requested Agent Host route does not exist.");
        }

        private async Task<AgentResponse> GetJobAsync(Guid id, CancellationToken cancellationToken)
        {
            var job = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return job == null ? JobNotFound() : JobResponse(200, job);
        }

        private async Task<AgentResponse> ApproveJobAsync(Guid id, CancellationToken cancellationToken)
        {
            var job = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job == null) return JobNotFound();
            if (job.State != JobState.AwaitingApproval || !job.PlanValidated)
            {
                return Error(409, "INVALID_JOB_STATE", "Only a job with a validated plan awaiting approval can be approved.");
            }

            return await TransitionAndSaveAsync(job, JobState.Approved, cancellationToken).ConfigureAwait(false);
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
            try
            {
                _stateMachine.Transition(job, nextState);
            }
            catch (JobStateTransitionException ex)
            {
                return Error(409, "INVALID_JOB_STATE", ex.Message);
            }

            await _repository.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
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
                outputPath = job.OutputPath,
                createdUtc = job.CreatedUtc,
                updatedUtc = job.UpdatedUtc
            });
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
