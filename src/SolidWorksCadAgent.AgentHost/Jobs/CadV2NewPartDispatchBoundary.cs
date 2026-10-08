using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Jobs;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>
    /// Owns the NewPart dispatch and immediately commits only the result returned by
    /// that dispatch. This boundary is not wired into JobCoordinator approval or v2
    /// execution; callers must keep it behind the existing execution gate.
    /// </summary>
    public sealed class CadV2NewPartDispatchBoundary
    {
        private readonly SqliteJobRepository _repository;
        private readonly IVersionedCadCommandExecutor _executor;
        // Entries also fence ambiguous dispatches whose durable outcome could not be recorded.
        // Those entries intentionally survive for this process's lifetime; startup recovery
        // must close unresolved Prepared attempts before dispatch in a new Host process.
        private static readonly ConcurrentDictionary<Guid, byte> GuardedAttempts = new ConcurrentDictionary<Guid, byte>();

        public CadV2NewPartDispatchBoundary(
            SqliteJobRepository repository,
            IVersionedCadCommandExecutor executor)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public async Task<CadCommandResult> DispatchAndCommitAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken cancellationToken)
        {
            if (!GuardedAttempts.TryAdd(attemptId, 0))
                throw new InvalidOperationException("This v2 mutation attempt has an in-progress or unresolved NewPart dispatch; replay is blocked.");
            var releaseGuard = true;
            try
            {
                var request = await PrepareRequestAsync(
                    jobId, revisionId, stepKey, attemptId, cancellationToken).ConfigureAwait(false);

                try
                {
                    // The executor may mutate even if it throws synchronously or returns no result.
                    // Only a confirmed durable outcome permits releasing this claim after invocation.
                    releaseGuard = false;
                    // Keep the returned object local to this one dispatch-to-commit path.
                    var observedResult = await _executor.ExecuteVersionedAsync(request, cancellationToken).ConfigureAwait(false);
                    if (observedResult == null || !observedResult.Success)
                        throw new InvalidOperationException("The NewPart dispatch returned no successful result; its mutation outcome is uncertain.");

                    await _repository.CommitV2NewPartOutcomeAsync(
                        jobId, revisionId, stepKey, request.Command.ManagedModelId.Value,
                        observedResult, cancellationToken).ConfigureAwait(false);
                    releaseGuard = true;
                    return observedResult;
                }
                catch (Exception dispatchFailure)
                {
                    releaseGuard = await MarkUncertainWithoutMaskingAsync(attemptId, dispatchFailure).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                if (releaseGuard) GuardedAttempts.TryRemove(attemptId, out _);
            }
        }

        private async Task<CadVersionedCommandRequest> PrepareRequestAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken cancellationToken)
        {
            if (jobId == Guid.Empty || revisionId == Guid.Empty || attemptId == Guid.Empty || string.IsNullOrWhiteSpace(stepKey))
                throw new InvalidOperationException("A non-empty job, revision, step and attempt identity is required.");

            var snapshot = await _repository.GetSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (snapshot == null || snapshot.Job == null || snapshot.Job.IsSimulated ||
                snapshot.Job.State != JobState.Executing || !snapshot.Job.PlanValidated)
                throw new InvalidOperationException("A real, plan-validated executing job is required for the NewPart dispatch.");

            var revision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (revision == null || revision.Id != revisionId || revision.JobId != jobId)
                throw new InvalidOperationException("The dispatch does not target the current immutable job revision.");

            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null ||
                parsed.CandidateV2.Steps.Count == 0)
                throw new InvalidOperationException("A normalized persisted version-2 plan is required for NewPart dispatch.");
            var step = parsed.CandidateV2.Steps[0];
            if (!string.Equals(step.StepKey, stepKey, StringComparison.Ordinal) ||
                !string.Equals(step.Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                step.OperationVersion != 1 || step.Parameters == null)
                throw new InvalidOperationException("Only the exact first operation-version-1 NewPart step can be dispatched by this boundary.");

            var attempt = await _repository.FindV2MutationAttemptAsync(
                jobId, revisionId, stepKey, cancellationToken).ConfigureAwait(false);
            if (attempt == null || attempt.Id != attemptId || attempt.JobId != jobId || attempt.RevisionId != revisionId ||
                attempt.StepKey != stepKey || attempt.Status != CadV2MutationAttemptStatus.Prepared ||
                !string.Equals(attempt.PlanSha256, HashPlan(revision.PlanJson), StringComparison.Ordinal))
                throw new InvalidOperationException("The dispatch does not match the exact Prepared attempt for the current stored plan.");

            var model = await _repository.GetModelAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false);
            if (model == null || model.Status != CadModelIdentityStatus.Pending ||
                model.CurrentModelRevisionId != attempt.ProspectiveModelRevisionId ||
                await _repository.GetV2ModelOwnerAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false) != jobId)
                throw new InvalidOperationException("The NewPart attempt does not own a pending model at its reserved revision.");

            return new CadVersionedCommandRequest(2, step.OperationVersion.Value, new CadCommandEnvelope
            {
                Command = step.Command,
                Parameters = (JObject)step.Parameters.DeepClone(),
                ExecutionId = jobId,
                ManagedModelId = attempt.ModelId
            });
        }

        private async Task<bool> MarkUncertainWithoutMaskingAsync(Guid attemptId, Exception dispatchFailure)
        {
            try
            {
                await _repository.MarkV2MutationAttemptUncertainAsync(attemptId, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception persistenceFailure)
            {
                // Preserve the original error and stack while exposing why its durable outcome
                // remains unresolved. The process guard stays held until process exit.
                dispatchFailure.Data["CadV2UncertainPersistenceFailure"] = persistenceFailure;
                return false;
            }
        }

        private static string HashPlan(string planJson)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(planJson)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
