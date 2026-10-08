using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Jobs;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public sealed class JobCoordinator
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadPlanningProvider _planningProvider;
        private readonly ICadCommandExecutor _executor;
        private readonly AgentSettings _settings;
        private readonly ExecutionMode _executionMode;
        private readonly Func<DateTime> _utcNow;
        private readonly JobStateMachine _stateMachine;
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _submissionSlots = new SemaphoreSlim(4, 4);
        private readonly object _submittedSync = new object();
        private readonly HashSet<Task> _submitted = new HashSet<Task>();

        public JobCoordinator(
            SqliteJobRepository repository,
            ICadPlanningProvider planningProvider,
            ICadCommandExecutor executor,
            AgentSettings settings)
            : this(repository, planningProvider, executor, settings, () => DateTime.UtcNow)
        {
        }

        internal JobCoordinator(
            SqliteJobRepository repository,
            ICadPlanningProvider planningProvider,
            ICadCommandExecutor executor,
            AgentSettings settings,
            Func<DateTime> utcNow)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _planningProvider = planningProvider ?? throw new ArgumentNullException(nameof(planningProvider));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _executionMode = _settings.ExecutionMode;
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _stateMachine = new JobStateMachine(_utcNow);
        }

        public string WorkspaceRoot => _settings.WorkspaceRoot;
        public bool SupportsImageInputs => _planningProvider is IImageCadPlanningProvider;

        public async Task<JobSnapshot> CreateAndPlanAsync(string prompt, CancellationToken cancellationToken, bool requiresExplicitApproval = false, CadPlanningImage image = null)
        {
            var job = await CreateNewJobAsync(prompt, cancellationToken, requiresExplicitApproval, image).ConfigureAwait(false);
            return await PlanCreatedJobAsync(job, cancellationToken).ConfigureAwait(false);
        }

        // Return a durable job before cloud planning, for clients with short transport deadlines.
        public async Task<JobSnapshot> SubmitAsync(string prompt, CancellationToken cancellationToken, CadPlanningImage image = null)
        {
            if (!_submissionSlots.Wait(0))
                throw new JobCoordinatorException("SUBMISSION_BUSY", "Four submitted jobs are already pending. Wait for a job to finish.");
            try
            {
                var job = await CreateNewJobAsync(prompt, cancellationToken, false, image).ConfigureAwait(false);
                var receipt = await SnapshotAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
                TrackBackground(job.Id, () => PlanCreatedJobAsync(job, cancellationToken));
                return receipt;
            }
            catch { _submissionSlots.Release(); throw; }
        }

        // All remote lifecycle work shares the same bounded queue and host shutdown drain.
        private void TrackBackground(Guid jobId, Func<Task<JobSnapshot>> action)
        {
            var work = Task.Run(async () =>
            {
                try { await action().ConfigureAwait(false); }
                catch
                {
                    foreach (var state in new[] { JobState.New, JobState.Interpreting, JobState.Approved, JobState.Executing, JobState.Verifying })
                        await FailIfCurrentAsync(jobId, state, CancellationToken.None).ConfigureAwait(false);
                }
                finally { _submissionSlots.Release(); }
            });
            lock (_submittedSync) _submitted.Add(work);
            _ = work.ContinueWith(completed =>
            {
                var observed = completed.Exception;
                lock (_submittedSync) _submitted.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task WaitForSubmittedJobsAsync()
        {
            lock (_submittedSync) return Task.WhenAll(_submitted.ToArray());
        }

        private async Task<CadJob> CreateNewJobAsync(string prompt, CancellationToken cancellationToken, bool requiresExplicitApproval = false, CadPlanningImage image = null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new JobCoordinatorException("PROMPT_REQUIRED", "A non-empty CAD prompt is required.");
            if (image != null && !SupportsImageInputs)
                throw new JobCoordinatorException("IMAGE_PLANNING_UNAVAILABLE", "The active CAD planner cannot interpret pictures.");

            var now = _utcNow();
            var job = new CadJob
            {
                Id = Guid.NewGuid(),
                Prompt = prompt.Trim(),
                State = JobState.New,
                IsSimulated = _executionMode == ExecutionMode.Simulation,
                RequiresExplicitApproval = requiresExplicitApproval,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            await _repository.CreateAsync(job, image, cancellationToken).ConfigureAwait(false);
            return job;
        }

        private async Task<JobSnapshot> PlanCreatedJobAsync(CadJob job, CancellationToken cancellationToken)
        {
            // Reload so a cancellation received immediately after submission cannot be resurrected.
            job = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (job.State != JobState.New) return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (!await TransitionAsync(job, JobState.Interpreting, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            CadPlanningResult plan = null;
            CadPlanV2Document version2Plan = null;
            try
            {
                var request = new CadPlanningRequest
                {
                    Prompt = job.Prompt,
                    Image = await _repository.GetInputImageAsync(job.Id, cancellationToken).ConfigureAwait(false)
                };
                if (_planningProvider is IVersionedCadPlanningProvider versionedProvider)
                    version2Plan = NormalizeVersion2Candidate(await versionedProvider.PlanDocumentAsync(request, cancellationToken).ConfigureAwait(false));
                else
                    plan = await _planningProvider.PlanAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await FailIfCurrentAsync(job.Id, JobState.Interpreting, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (current == null || current.State == JobState.Cancelled)
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
            job = current;

            if (version2Plan != null)
                return await PersistVersion2ReviewCandidateAsync(job, version2Plan, 1, job.Prompt, null, false, cancellationToken).ConfigureAwait(false);

            plan = NormalizePlan(plan);
            var validationAmbiguities = ValidatePlan(plan);
            foreach (var ambiguity in validationAmbiguities)
                plan.Ambiguities.Add(ambiguity);
            job.OverwriteRequested = CadPlanningCommandContract.RequestsOverwrite(plan.ProposedCommands);

            var revision = new JobRevision
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                RevisionNumber = 1,
                Prompt = job.Prompt,
                InterpretationJson = JsonConvert.SerializeObject(new
                {
                    plan.Summary,
                    plan.Assumptions,
                    plan.Ambiguities,
                    plan.Provider,
                    plan.Model,
                    plan.Usage
                }),
                PlanJson = JsonConvert.SerializeObject(plan),
                CreatedUtc = _utcNow()
            };
            await _repository.AppendRevisionAsync(revision, cancellationToken).ConfigureAwait(false);

            job.PlanValidated = plan.Ambiguities.Count == 0 && plan.ProposedCommands.Count > 0;
            job.HasUnresolvedAmbiguity = plan.Ambiguities.Count > 0;
            job.AmbiguityMessage = job.HasUnresolvedAmbiguity ? string.Join(Environment.NewLine, plan.Ambiguities) : null;
            var plannedState = job.HasUnresolvedAmbiguity || !job.PlanValidated
                ? JobState.AwaitingClarification
                : JobState.AwaitingApproval;
            if (!await TransitionAsync(job, plannedState, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            if (_settings.AutoMode && ApprovalPolicy.CanExecute(job, _settings))
            {
                if (!await TransitionAsync(job, JobState.Approved, cancellationToken).ConfigureAwait(false))
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
                return await ExecuteApprovedAsync(job, revision, plan, cancellationToken).ConfigureAwait(false);
            }

            return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        public Task<JobSnapshot> ApproveAndExecuteAsync(Guid jobId, Guid revisionId, CancellationToken cancellationToken) =>
            ApproveAsync(jobId, revisionId, cancellationToken, false);

        public Task<JobSnapshot> EnqueueApprovalAsync(Guid jobId, Guid revisionId, CancellationToken cancellationToken) =>
            WithBackgroundSlotAsync(() => ApproveAsync(jobId, revisionId, cancellationToken, true));

        private async Task<JobSnapshot> WithBackgroundSlotAsync(Func<Task<JobSnapshot>> prepare)
        {
            if (!_submissionSlots.Wait(0))
                throw new JobCoordinatorException("SUBMISSION_BUSY", "Four lifecycle operations are already pending. Wait for work to finish.");
            try { return await prepare().ConfigureAwait(false); }
            catch { _submissionSlots.Release(); throw; }
        }

        private async Task<JobSnapshot> ApproveAsync(Guid jobId, Guid revisionId, CancellationToken cancellationToken, bool enqueue)
        {
            var snapshot = await SnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                throw new JobCoordinatorException("JOB_NOT_FOUND", "The requested CAD job does not exist.");

            var revision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (revision == null || revision.Id != revisionId)
                throw new JobCoordinatorException("STALE_PLAN", "The approved plan is no longer the current CAD job revision.");
            if (snapshot.Job.State != JobState.AwaitingApproval || !snapshot.Job.PlanValidated)
                throw new JobCoordinatorException("INVALID_JOB_STATE", "The CAD job is not ready for approval.");
            if (snapshot.Job.OverwriteRequested && !snapshot.Job.OverwriteAuthorized)
                throw new JobCoordinatorException("OVERWRITE_AUTHORIZATION_REQUIRED", "The current plan requests overwrite permission that has not been explicitly authorized.");

            var parsedPlan = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsedPlan.IsValid)
            {
                var errorCode = parsedPlan.Errors.Any(error => error.StartsWith("UNSUPPORTED_PLAN_VERSION", StringComparison.Ordinal))
                    ? "UNSUPPORTED_PLAN_VERSION"
                    : "INVALID_PLAN";
                throw new JobCoordinatorException(errorCode, "The persisted CAD plan could not be accepted: " + string.Join(" ", parsedPlan.Errors));
            }
            if (parsedPlan.Version != 1)
                throw new JobCoordinatorException("UNSUPPORTED_PLAN_VERSION",
                    "This CAD Agent can approve only historical unversioned version-1 plans; version-2 candidates are not executable yet.");
            if (parsedPlan.LegacyPlan == null)
                throw new JobCoordinatorException("INVALID_PLAN", "The persisted CAD plan could not be loaded.");
            var plan = parsedPlan.LegacyPlan;
            plan = NormalizePlan(plan);
            RequireCurrentPlanValid(plan);

            if (!await TransitionAsync(snapshot.Job, JobState.Approved, cancellationToken, revisionId).ConfigureAwait(false))
                throw new JobCoordinatorException("CONCURRENT_JOB_UPDATE", "The CAD job changed while approval was being processed.");

            if (enqueue)
            {
                var receipt = await SnapshotAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                TrackBackground(jobId, () => ExecuteApprovedAsync(snapshot.Job, revision, plan, cancellationToken));
                return receipt;
            }
            return await ExecuteApprovedAsync(snapshot.Job, revision, plan, cancellationToken).ConfigureAwait(false);
        }

        public Task<JobSnapshot> RequestChangesAsync(Guid jobId, Guid expectedRevisionId, string instructions, CancellationToken cancellationToken) =>
            ChangeAsync(jobId, expectedRevisionId, instructions, cancellationToken, false);

        public Task<JobSnapshot> EnqueueChangesAsync(Guid jobId, Guid expectedRevisionId, string instructions, CancellationToken cancellationToken) =>
            WithBackgroundSlotAsync(() => ChangeAsync(jobId, expectedRevisionId, instructions, cancellationToken, true));

        private async Task<JobSnapshot> ChangeAsync(
            Guid jobId,
            Guid expectedRevisionId,
            string instructions,
            CancellationToken cancellationToken, bool enqueue)
        {
            if (string.IsNullOrWhiteSpace(instructions))
                throw new JobCoordinatorException("INSTRUCTIONS_REQUIRED", "Change instructions are required.");

            var snapshot = await SnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                throw new JobCoordinatorException("JOB_NOT_FOUND", "The requested CAD job does not exist.");

            var currentRevision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (currentRevision == null || currentRevision.Id != expectedRevisionId)
                throw new JobCoordinatorException("STALE_PLAN", "The requested changes do not target the current CAD job revision.");
            if (snapshot.Job.State != JobState.AwaitingApproval && snapshot.Job.State != JobState.AwaitingClarification && snapshot.Job.State != JobState.ReadyForReview)
                throw new JobCoordinatorException("INVALID_JOB_STATE", "Changes require a pending plan or a result ready for review.");

            var rebuildResult = snapshot.Job.State == JobState.ReadyForReview ||
                snapshot.Commands.Any(command => command.Success && command.CommandName == CadCommandNames.NewPart);
            if (rebuildResult)
                instructions += "\nBuild a complete replacement in a NEW part, leaving the previous result untouched. Save a separate revision file without overwrite.";

            if (!await TransitionAsync(snapshot.Job, JobState.Interpreting, cancellationToken, expectedRevisionId).ConfigureAwait(false))
                throw new JobCoordinatorException("CONCURRENT_JOB_UPDATE", "The CAD job changed while revision was being requested.");
            if (enqueue)
            {
                var receipt = await SnapshotAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                TrackBackground(jobId, () => ReplanAsync(snapshot, expectedRevisionId, instructions, cancellationToken, rebuildResult));
                return receipt;
            }
            return await ReplanAsync(snapshot, expectedRevisionId, instructions, cancellationToken, rebuildResult).ConfigureAwait(false);
        }

        private async Task<JobSnapshot> ReplanAsync(JobSnapshot snapshot, Guid expectedRevisionId, string instructions, CancellationToken cancellationToken, bool rebuildResult = false)
        {
            var jobId = snapshot.Job.Id;
            var currentRevision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).Last();
            var expectedState = JobState.Interpreting;
            var trimmedInstructions = instructions.Trim();
            var clarifications = snapshot.Revisions
                .OrderBy(item => item.RevisionNumber)
                .Skip(1)
                .Select(item => item.Prompt)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Concat(new[] { trimmedInstructions })
                .ToList();

            CadPlanningResult plan = null;
            CadPlanV2Document version2Plan = null;
            try
            {
                var request = new CadPlanningRequest
                {
                    Prompt = snapshot.Job.Prompt,
                    Clarifications = clarifications,
                    Image = await _repository.GetInputImageAsync(jobId, cancellationToken).ConfigureAwait(false)
                };
                if (_planningProvider is IVersionedCadPlanningProvider versionedProvider)
                    version2Plan = NormalizeVersion2Candidate(await versionedProvider.PlanDocumentAsync(request, cancellationToken).ConfigureAwait(false));
                else
                    plan = await _planningProvider.PlanAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await FailIfCurrentAsync(jobId, expectedState, cancellationToken).ConfigureAwait(false);
                throw;
            }

            if (version2Plan != null)
                return await PersistVersion2ReviewCandidateAsync(snapshot.Job, version2Plan,
                    currentRevision.RevisionNumber + 1, trimmedInstructions, expectedRevisionId, rebuildResult, cancellationToken).ConfigureAwait(false);

            plan = NormalizePlan(plan);
            if (rebuildResult)
            {
                if (plan.ProposedCommands.FirstOrDefault()?.Command != CadCommandNames.NewPart)
                    plan.Ambiguities.Add("A revised result must start with NewPart to preserve the previous result.");
                if (plan.ProposedCommands.Any(command => command.Command == CadCommandNames.OpenPart))
                    plan.Ambiguities.Add("A replacement result cannot open an existing part; build it entirely in the new part.");
                if (!plan.ProposedCommands.Any(command => command.Command == CadCommandNames.SavePart))
                    plan.Ambiguities.Add("A replacement result must save a separate native part file.");
                foreach (var save in plan.ProposedCommands.Where(command => command.Command == CadCommandNames.SavePart))
                {
                    var path = (string)save.Parameters["path"];
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var extension = System.IO.Path.GetExtension(path);
                        save.Parameters["path"] = path.Substring(0, path.Length - extension.Length) + "-r" + (currentRevision.RevisionNumber + 1) + "-" + jobId.ToString("N").Substring(0, 8) + extension;
                        save.Parameters["allowOverwrite"] = false;
                    }
                }
                plan.Assumptions.Add("The revised result is created as a separate part and file; the previous result remains unchanged.");
            }
            foreach (var ambiguity in ValidatePlan(plan))
                plan.Ambiguities.Add(ambiguity);

            var job = snapshot.Job;
            job.PlanValidated = plan.Ambiguities.Count == 0 && plan.ProposedCommands.Count > 0;
            job.HasUnresolvedAmbiguity = plan.Ambiguities.Count > 0;
            job.AmbiguityMessage = job.HasUnresolvedAmbiguity ? string.Join(Environment.NewLine, plan.Ambiguities) : null;
            job.OverwriteRequested = CadPlanningCommandContract.RequestsOverwrite(plan.ProposedCommands);
            job.OverwriteAuthorized = false;
            _stateMachine.Transition(
                job,
                job.HasUnresolvedAmbiguity || !job.PlanValidated
                    ? JobState.AwaitingClarification
                    : JobState.AwaitingApproval);

            var revision = new JobRevision
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                RevisionNumber = currentRevision.RevisionNumber + 1,
                Prompt = trimmedInstructions,
                InterpretationJson = JsonConvert.SerializeObject(new
                {
                    plan.Summary,
                    plan.Assumptions,
                    plan.Ambiguities,
                    plan.Provider,
                    plan.Model,
                    plan.Usage
                }),
                PlanJson = JsonConvert.SerializeObject(plan),
                CreatedUtc = _utcNow()
            };

            var appended = await _repository.TryAppendRevisionAsync(
                job,
                expectedState,
                expectedRevisionId,
                revision,
                cancellationToken).ConfigureAwait(false);
            if (!appended)
                throw new JobCoordinatorException("CONCURRENT_JOB_UPDATE", "The CAD job changed while the revision was being planned.");

            return await SnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<JobSnapshot> ExecuteApprovedAsync(
            CadJob job,
            JobRevision revision,
            CadPlanningResult plan,
            CancellationToken cancellationToken)
        {
            await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
                if (current == null || current.State == JobState.Cancelled)
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
                return await ExecuteApprovedWithinGateAsync(current, revision, plan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _executionGate.Release();
            }
        }

        private async Task<JobSnapshot> ExecuteApprovedWithinGateAsync(
            CadJob job,
            JobRevision revision,
            CadPlanningResult plan,
            CancellationToken cancellationToken)
        {
            if (!ApprovalPolicy.CanExecute(job, _settings))
                throw new JobCoordinatorException("APPROVAL_REQUIRED", "The CAD job has not passed the execution approval policy.");
            RequireCurrentPlanValid(plan);
            if (!await TransitionAsync(job, JobState.Executing, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            var sequence = 0;
            CadModelIdentityRecord managedModel = null;
            foreach (var command in plan.ProposedCommands)
            {
                var current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
                if (current == null || current.State == JobState.Cancelled)
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

                if (_executionMode == ExecutionMode.Real && command.Command == CadCommandNames.NewPart)
                {
                    managedModel = CreatePendingModelIdentity();
                    await _repository.RegisterModelAsync(managedModel, cancellationToken).ConfigureAwait(false);
                }

                var modelRevisionId = managedModel != null && ChangesManagedModel(command.Command) ? Guid.NewGuid() : (Guid?)null;
                var outputEntityId = managedModel != null && command.Command == CadCommandNames.CreateSketch ? Guid.NewGuid() : (Guid?)null;
                if (outputEntityId.HasValue)
                {
                    await _repository.AddEntityBindingAsync(CreatePendingSketchBinding(managedModel, outputEntityId.Value, modelRevisionId.Value), cancellationToken)
                        .ConfigureAwait(false);
                }

                var executableCommand = PrepareForExecution(current, command, managedModel?.ModelId, outputEntityId);
                CadCommandResult result;
                try
                {
                    result = await ExecuteAndRecordAsync(job.Id, revision.RevisionNumber, ++sequence, executableCommand, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await MarkIdentityUncertainAsync(managedModel, outputEntityId, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                if (!result.Success)
                {
                    await MarkIdentityUncertainAsync(managedModel, outputEntityId, CancellationToken.None).ConfigureAwait(false);
                    await FailIfCurrentAsync(job.Id, JobState.Executing, cancellationToken).ConfigureAwait(false);
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
                }

                if (managedModel != null)
                {
                    try
                    {
                        await UpdateManagedModelAfterCommandAsync(managedModel, command.Command, result, modelRevisionId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await MarkIdentityUncertainAsync(managedModel, outputEntityId, CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
            }

            job = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (job.State == JobState.Cancelled || !await TransitionAsync(job, JobState.Verifying, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            var verificationPassed = await VerifyAsync(job, revision.RevisionNumber, sequence, managedModel?.ModelId, cancellationToken).ConfigureAwait(false);
            job = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (job.State == JobState.Cancelled)
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            await TransitionAsync(
                job,
                verificationPassed ? JobState.ReadyForReview : JobState.Failed,
                cancellationToken).ConfigureAwait(false);
            return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> VerifyAsync(
            CadJob job,
            int revisionNumber,
            int sequence,
            Guid? managedModelId,
            CancellationToken cancellationToken)
        {
            var checks = new[]
            {
                new { Name = "BodyCount", Command = CadCommandNames.GetBodyCount },
                new { Name = "BoundingBox", Command = CadCommandNames.GetBoundingBox },
                new { Name = "RebuildErrors", Command = CadCommandNames.GetRebuildErrors }
            };
            var allPassed = true;
            foreach (var check in checks)
            {
                var command = new CadCommandEnvelope { Command = check.Command, Parameters = new JObject(), ManagedModelId = managedModelId };
                var result = await ExecuteAndRecordAsync(job.Id, revisionNumber, ++sequence, command, cancellationToken)
                    .ConfigureAwait(false);
                var passed = EvaluateVerification(check.Name, result);
                allPassed &= passed;
                await _repository.AppendVerificationAsync(new VerificationResultRecord
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    RevisionNumber = revisionNumber,
                    CheckName = check.Name,
                    Passed = passed,
                    ExpectedJson = ExpectedVerification(check.Name).ToString(Formatting.None),
                    ActualJson = (result.Data ?? new JObject()).ToString(Formatting.None),
                    CreatedUtc = _utcNow()
                }, cancellationToken).ConfigureAwait(false);
                if (!passed) break;
            }
            return allPassed;
        }

        private async Task<CadCommandResult> ExecuteAndRecordAsync(
            Guid jobId,
            int revisionNumber,
            int sequence,
            CadCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var started = _utcNow();
            var commandToExecute = command;
            if (LegacyCadOperationAdapter.TryAdapt(command, out var operation, out var adaptationError))
            {
                commandToExecute = operation.ToCommandEnvelope();
            }

            var result = adaptationError == null
                ? await _executor.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = commandToExecute.Command,
                    Parameters = commandToExecute.Parameters,
                    ExecutionId = jobId,
                    ManagedModelId = command.ManagedModelId,
                    OutputEntityId = command.OutputEntityId
                }, cancellationToken).ConfigureAwait(false)
                : new CadCommandResult
                {
                    Success = false,
                    Data = new JObject(),
                    Error = adaptationError
                };
            await _repository.AppendCommandAsync(new CommandExecutionRecord
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                RevisionNumber = revisionNumber,
                SequenceNumber = sequence,
                CommandName = command.Command,
                ParametersJson = (command.Parameters ?? new JObject()).ToString(Formatting.None),
                Success = result.Success,
                ResultJson = (result.Data ?? new JObject()).ToString(Formatting.None),
                ErrorCode = result.Error?.Code,
                ErrorMessage = result.Error?.Message,
                StartedUtc = started,
                CompletedUtc = _utcNow()
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task<bool> TransitionAsync(CadJob job, JobState nextState, CancellationToken cancellationToken, Guid? revisionId = null)
        {
            var expected = job.State;
            _stateMachine.Transition(job, nextState);
            return await _repository.TryUpdateFromStateAsync(job, expected, cancellationToken, revisionId).ConfigureAwait(false);
        }

        private async Task FailIfCurrentAsync(Guid jobId, JobState expectedState, CancellationToken cancellationToken)
        {
            var job = await _repository.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job != null && job.State == expectedState)
                await TransitionAsync(job, JobState.Failed, cancellationToken).ConfigureAwait(false);
        }

        private Task<JobSnapshot> SnapshotAsync(Guid jobId, CancellationToken cancellationToken)
        {
            return _repository.GetSnapshotAsync(jobId, cancellationToken);
        }

        private List<string> ValidatePlan(CadPlanningResult plan)
        {
            var errors = new List<string>();
            if (plan == null)
            {
                errors.Add("The planning provider returned no plan.");
                return errors;
            }
            if (plan.ProposedCommands == null || plan.ProposedCommands.Count == 0)
            {
                if (plan.Ambiguities == null || plan.Ambiguities.Count == 0)
                    errors.Add("The planning provider returned no CAD commands.");
                return errors;
            }
            foreach (var command in plan.ProposedCommands)
            {
                if (LegacyCadOperationAdapter.TryAdapt(command, out _, out var adaptationError))
                    continue;
                if (adaptationError != null)
                {
                    errors.Add(adaptationError.Message);
                    continue;
                }

                var error = CadPlanningCommandContract.Validate(command);
                if (error != null) errors.Add(error);
            }
            errors.AddRange(CadPlanLifecycleValidator.Validate(plan.ProposedCommands, _executionMode));
            return errors;
        }

        private static CadPlanV2Document NormalizeVersion2Candidate(string json)
        {
            var parsed = CadPlanDocumentReader.ReadCandidate(json);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                throw new JobCoordinatorException("INVALID_PLAN", "The versioned planning provider must return a valid unnormalized version-2 candidate: " + string.Join(" ", parsed.Errors));
            return CadPlanV2HostNormalizer.Normalize(parsed.CandidateV2);
        }

        private async Task<JobSnapshot> PersistVersion2ReviewCandidateAsync(
            CadJob job,
            CadPlanV2Document plan,
            int revisionNumber,
            string prompt,
            Guid? expectedRevisionId,
            bool replacementResult,
            CancellationToken cancellationToken)
        {
            const string reviewOnly = "Version-2 plans are stored for review only. Approval and execution remain disabled until whole-plan execution and verification are available.";
            const string simulationUnsupported = "Version-2 feature plans are unsupported in simulation because the simulator cannot resolve profileSketch references or verify resulting feature geometry.";
            plan.Ambiguities = plan.Ambiguities ?? new List<string>();
            plan.Ambiguities.RemoveAll(item => string.Equals(item, reviewOnly, StringComparison.Ordinal));
            plan.Ambiguities.Add(reviewOnly);
            plan.Ambiguities.RemoveAll(item => string.Equals(item, simulationUnsupported, StringComparison.Ordinal));
            if (_executionMode == ExecutionMode.Simulation)
                plan.Ambiguities.Add(simulationUnsupported);
            if (replacementResult)
            {
                plan.Assumptions = plan.Assumptions ?? new List<string>();
                plan.Assumptions.Add("The previous result remains unchanged; this version-2 candidate is a proposed replacement only.");
            }

            job.PlanValidated = false;
            job.HasUnresolvedAmbiguity = true;
            job.AmbiguityMessage = string.Join(Environment.NewLine, plan.Ambiguities);
            job.OverwriteRequested = CadPlanningCommandContract.RequestsOverwrite(plan.Steps.Select(step => new CadCommandEnvelope
            {
                Command = step.Command,
                Parameters = step.Parameters
            }).ToList());
            job.OverwriteAuthorized = false;

            var revision = new JobRevision
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                RevisionNumber = revisionNumber,
                Prompt = prompt,
                InterpretationJson = JsonConvert.SerializeObject(new
                {
                    plan.Summary,
                    plan.Assumptions,
                    plan.Ambiguities,
                    PlanVersion = plan.PlanVersion,
                    ExecutionAvailable = false
                }),
                PlanJson = JsonConvert.SerializeObject(plan),
                CreatedUtc = _utcNow()
            };

            if (expectedRevisionId.HasValue)
            {
                _stateMachine.Transition(job, JobState.AwaitingClarification);
                if (!await _repository.TryAppendRevisionAsync(job, JobState.Interpreting, expectedRevisionId.Value, revision, cancellationToken).ConfigureAwait(false))
                    throw new JobCoordinatorException("CONCURRENT_JOB_UPDATE", "The CAD job changed while the version-2 candidate was being persisted.");
            }
            else
            {
                await _repository.AppendRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
                if (!await TransitionAsync(job, JobState.AwaitingClarification, cancellationToken).ConfigureAwait(false))
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }

            return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        private void RequireCurrentPlanValid(CadPlanningResult plan)
        {
            var errors = ValidatePlan(plan);
            if (errors.Count > 0)
                throw new JobCoordinatorException("INVALID_PLAN", "The saved CAD plan no longer passes current preflight: " + string.Join(" ", errors));
        }

        private CadCommandEnvelope PrepareForExecution(CadJob job, CadCommandEnvelope command, Guid? managedModelId, Guid? outputEntityId)
        {
            var parameters = command.Parameters == null ? new JObject() : (JObject)command.Parameters.DeepClone();
            if (command.Command == CadCommandNames.SavePart)
                parameters["allowOverwrite"] = job.OverwriteAuthorized;
            return new CadCommandEnvelope
            {
                Command = command.Command,
                Parameters = parameters,
                ManagedModelId = managedModelId,
                OutputEntityId = outputEntityId
            };
        }

        private CadModelIdentityRecord CreatePendingModelIdentity()
        {
            var now = _utcNow();
            return new CadModelIdentityRecord
            {
                ModelId = Guid.NewGuid(),
                DocumentKind = "Part",
                Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId",
                CurrentModelRevisionId = Guid.NewGuid(),
                ConfigurationKey = "Pending",
                RegistryVersion = 1,
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }

        private CadEntityReferenceBinding CreatePendingSketchBinding(CadModelIdentityRecord model, Guid entityId, Guid revisionId)
        {
            var now = _utcNow();
            return new CadEntityReferenceBinding
            {
                ModelId = model.ModelId,
                EntityId = entityId,
                EntityKind = "Sketch",
                ConfigurationKey = model.ConfigurationKey,
                NativeObjectKind = "SketchFeature",
                ReferenceFormatVersion = 3,
                CreatedAtModelRevisionId = revisionId,
                Status = CadEntityReferenceStatus.Pending,
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }

        private async Task UpdateManagedModelAfterCommandAsync(
            CadModelIdentityRecord model,
            string command,
            CadCommandResult result,
            Guid? modelRevisionId,
            CancellationToken cancellationToken)
        {
            if (modelRevisionId.HasValue) model.CurrentModelRevisionId = modelRevisionId.Value;
            if (command == CadCommandNames.NewPart)
            {
                model.ConfigurationKey = (string)result.Data?["configurationKey"] ?? "Default";
                model.Status = CadModelIdentityStatus.ActiveUnsaved;
            }
            else if (command == CadCommandNames.SavePart)
            {
                var path = (string)result.Data?["path"];
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("SavePart succeeded without returning a saved path for the managed model.");
                model.CanonicalPath = Path.GetFullPath(path);
                model.LastSavedSha256 = ComputeFileSha256(model.CanonicalPath);
                model.Status = CadModelIdentityStatus.ActiveSaved;
            }
            else if (model.Status != CadModelIdentityStatus.Pending)
            {
                model.Status = CadModelIdentityStatus.ActiveUnsaved;
            }
            model.UpdatedUtc = _utcNow();
            await _repository.UpdateModelAsync(model, cancellationToken).ConfigureAwait(false);
        }

        private async Task MarkIdentityUncertainAsync(CadModelIdentityRecord model, Guid? outputEntityId, CancellationToken cancellationToken)
        {
            if (model == null) return;
            model.Status = CadModelIdentityStatus.Uncertain;
            model.UpdatedUtc = _utcNow();
            await _repository.UpdateModelAsync(model, cancellationToken).ConfigureAwait(false);
            if (outputEntityId.HasValue)
            {
                var binding = await _repository.GetEntityBindingAsync(model.ModelId, outputEntityId.Value, model.ConfigurationKey, cancellationToken)
                    .ConfigureAwait(false);
                if (binding != null)
                {
                    binding.Status = CadEntityReferenceStatus.Uncertain;
                    binding.UpdatedUtc = _utcNow();
                    await _repository.UpdateEntityBindingAsync(binding, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool ChangesManagedModel(string command)
        {
            switch (command)
            {
                case CadCommandNames.NewPart:
                case CadCommandNames.CreateSketch:
                case CadCommandNames.AddLine:
                case CadCommandNames.AddArc:
                case CadCommandNames.AddRectangle:
                case CadCommandNames.AddCircle:
                case CadCommandNames.AddSlot:
                case CadCommandNames.AddRegularPolygon:
                case CadCommandNames.ExitSketch:
                case CadCommandNames.Extrude:
                case CadCommandNames.CutExtrude:
                    return true;
                default:
                    return false;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static CadPlanningResult NormalizePlan(CadPlanningResult plan)
        {
            if (plan == null)
            {
                return new CadPlanningResult
                {
                    Summary = "The planning provider returned no plan.",
                    Ambiguities = new List<string> { "The planning provider returned no plan." }
                };
            }

            plan.Assumptions = plan.Assumptions ?? new List<string>();
            plan.Ambiguities = plan.Ambiguities ?? new List<string>();
            plan.ProposedCommands = plan.ProposedCommands ?? new List<CadCommandEnvelope>();
            plan.Usage = plan.Usage ?? new CadPlanningUsage();
            return plan;
        }

        private static bool EvaluateVerification(string name, CadCommandResult result)
        {
            if (result == null || !result.Success) return false;
            if (name == "BodyCount") return (int?)result.Data?["bodyCount"] == 1;
            if (name == "BoundingBox")
            {
                return (double?)result.Data?["sizeXmm"] > 0 &&
                       (double?)result.Data?["sizeYmm"] > 0 &&
                       (double?)result.Data?["sizeZmm"] > 0;
            }
            if (name == "RebuildErrors") return (bool?)result.Data?["hasErrors"] == false;
            return false;
        }

        private static JObject ExpectedVerification(string name)
        {
            if (name == "BodyCount") return JObject.FromObject(new { bodyCount = 1 });
            if (name == "BoundingBox") return JObject.FromObject(new { positiveExtents = true });
            return JObject.FromObject(new { hasErrors = false });
        }
    }
}
