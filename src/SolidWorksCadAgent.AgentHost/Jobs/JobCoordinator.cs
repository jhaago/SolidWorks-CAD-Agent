using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
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
        private readonly Func<DateTime> _utcNow;
        private readonly JobStateMachine _stateMachine;
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);

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
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _stateMachine = new JobStateMachine(_utcNow);
        }

        public async Task<JobSnapshot> CreateAndPlanAsync(string prompt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new JobCoordinatorException("PROMPT_REQUIRED", "A non-empty CAD prompt is required.");

            var now = _utcNow();
            var job = new CadJob
            {
                Id = Guid.NewGuid(),
                Prompt = prompt.Trim(),
                State = JobState.New,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            await _repository.CreateAsync(job, cancellationToken).ConfigureAwait(false);
            if (!await TransitionAsync(job, JobState.Interpreting, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            CadPlanningResult plan;
            try
            {
                plan = await _planningProvider.PlanAsync(
                    new CadPlanningRequest { Prompt = job.Prompt },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await FailIfCurrentAsync(job.Id, JobState.Interpreting, cancellationToken).ConfigureAwait(false);
                throw;
            }

            plan = NormalizePlan(plan);

            var current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (current == null || current.State == JobState.Cancelled)
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
            job = current;

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

        public async Task<JobSnapshot> ApproveAndExecuteAsync(
            Guid jobId,
            Guid revisionId,
            CancellationToken cancellationToken)
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

            var plan = JsonConvert.DeserializeObject<CadPlanningResult>(revision.PlanJson);
            if (plan == null)
                throw new JobCoordinatorException("INVALID_PLAN", "The persisted CAD plan could not be loaded.");
            plan = NormalizePlan(plan);

            if (!await TransitionAsync(snapshot.Job, JobState.Approved, cancellationToken).ConfigureAwait(false))
                throw new JobCoordinatorException("CONCURRENT_JOB_UPDATE", "The CAD job changed while approval was being processed.");

            return await ExecuteApprovedAsync(snapshot.Job, revision, plan, cancellationToken).ConfigureAwait(false);
        }

        public async Task<JobSnapshot> RequestChangesAsync(
            Guid jobId,
            Guid expectedRevisionId,
            string instructions,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(instructions))
                throw new JobCoordinatorException("INSTRUCTIONS_REQUIRED", "Change instructions are required.");

            var snapshot = await SnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                throw new JobCoordinatorException("JOB_NOT_FOUND", "The requested CAD job does not exist.");

            var currentRevision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (currentRevision == null || currentRevision.Id != expectedRevisionId)
                throw new JobCoordinatorException("STALE_PLAN", "The requested changes do not target the current CAD job revision.");
            if (snapshot.Job.State != JobState.AwaitingApproval && snapshot.Job.State != JobState.AwaitingClarification)
                throw new JobCoordinatorException("INVALID_JOB_STATE", "Changes can only be requested before CAD execution.");

            var expectedState = snapshot.Job.State;
            var trimmedInstructions = instructions.Trim();
            var clarifications = snapshot.Revisions
                .OrderBy(item => item.RevisionNumber)
                .Skip(1)
                .Select(item => item.Prompt)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Concat(new[] { trimmedInstructions })
                .ToList();

            CadPlanningResult plan;
            try
            {
                plan = await _planningProvider.PlanAsync(new CadPlanningRequest
                {
                    Prompt = snapshot.Job.Prompt,
                    Clarifications = clarifications
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await FailIfCurrentAsync(jobId, expectedState, cancellationToken).ConfigureAwait(false);
                throw;
            }

            plan = NormalizePlan(plan);
            foreach (var ambiguity in ValidatePlan(plan))
                plan.Ambiguities.Add(ambiguity);

            var job = snapshot.Job;
            _stateMachine.Transition(job, JobState.Interpreting);
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
            if (!await TransitionAsync(job, JobState.Executing, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            var sequence = 0;
            foreach (var command in plan.ProposedCommands)
            {
                var current = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
                if (current == null || current.State == JobState.Cancelled)
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

                var executableCommand = PrepareForExecution(current, command);
                var result = await ExecuteAndRecordAsync(job.Id, revision.RevisionNumber, ++sequence, executableCommand, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    await FailIfCurrentAsync(job.Id, JobState.Executing, cancellationToken).ConfigureAwait(false);
                    return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            job = await _repository.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (job.State == JobState.Cancelled || !await TransitionAsync(job, JobState.Verifying, cancellationToken).ConfigureAwait(false))
                return await SnapshotAsync(job.Id, cancellationToken).ConfigureAwait(false);

            var verificationPassed = await VerifyAsync(job, revision.RevisionNumber, sequence, cancellationToken).ConfigureAwait(false);
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
                var command = new CadCommandEnvelope { Command = check.Command, Parameters = new JObject() };
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
            var result = await _executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
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

        private async Task<bool> TransitionAsync(CadJob job, JobState nextState, CancellationToken cancellationToken)
        {
            var expected = job.State;
            _stateMachine.Transition(job, nextState);
            return await _repository.TryUpdateFromStateAsync(job, expected, cancellationToken).ConfigureAwait(false);
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

        private static List<string> ValidatePlan(CadPlanningResult plan)
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
                var error = CadPlanningCommandContract.Validate(command);
                if (error != null) errors.Add(error);
            }
            return errors;
        }

        private static CadCommandEnvelope PrepareForExecution(CadJob job, CadCommandEnvelope command)
        {
            if (command.Command != CadCommandNames.SavePart) return command;
            var parameters = command.Parameters == null ? new JObject() : (JObject)command.Parameters.DeepClone();
            parameters["allowOverwrite"] = job.OverwriteAuthorized;
            return new CadCommandEnvelope { Command = command.Command, Parameters = parameters };
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
