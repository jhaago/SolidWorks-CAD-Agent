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
    /// Dispatches exactly the prepared managed CreateSketch operation and commits only the
    /// matching output identity returned after the Bridge persists its native reference.
    /// This boundary is deliberately not connected to general v2 JobCoordinator execution.
    /// </summary>
    public sealed class CadV2CreateSketchDispatchBoundary
    {
        private readonly SqliteJobRepository _repository;
        private readonly IVersionedCadCommandExecutor _executor;
        private static readonly ConcurrentDictionary<Guid, byte> GuardedAttempts = CadV2DispatchFence.Attempts;

        public CadV2CreateSketchDispatchBoundary(SqliteJobRepository repository, IVersionedCadCommandExecutor executor)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public async Task<CadCommandResult> DispatchAndCommitAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken cancellationToken)
        {
            if (!GuardedAttempts.TryAdd(attemptId, 0))
                throw new InvalidOperationException("This v2 sketch attempt has an in-progress or unresolved dispatch; replay is blocked.");
            var releaseGuard = true;
            try
            {
                var request = await PrepareRequestAsync(jobId, revisionId, stepKey, attemptId, cancellationToken).ConfigureAwait(false);
                try
                {
                    releaseGuard = false;
                    var observedResult = await _executor.ExecuteVersionedAsync(request, cancellationToken).ConfigureAwait(false);
                    if (observedResult == null || !observedResult.Success || observedResult.Data == null ||
                        !Guid.TryParse(observedResult.Data.Value<string>("entityId"), out var observedEntityId) ||
                        observedEntityId != request.Command.OutputEntityId)
                        throw new InvalidOperationException("The CreateSketch dispatch did not return the exact prepared output entity identity.");

                    await _repository.CommitV2SketchOutcomeAsync(
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
                throw new InvalidOperationException("A real, plan-validated executing job is required for the CreateSketch dispatch.");
            var revision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (revision == null || revision.Id != revisionId || revision.JobId != jobId)
                throw new InvalidOperationException("The dispatch does not target the current immutable job revision.");

            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null || parsed.CandidateV2.Steps.Count < 2)
                throw new InvalidOperationException("A normalized persisted v2 plan with NewPart followed by CreateSketch is required.");
            var newPartStep = parsed.CandidateV2.Steps[0];
            var sketchStep = parsed.CandidateV2.Steps[1];
            if (!string.Equals(newPartStep.Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                !string.Equals(sketchStep.StepKey, stepKey, StringComparison.Ordinal) ||
                !string.Equals(sketchStep.Command, CadCommandNames.CreateSketch, StringComparison.Ordinal) ||
                sketchStep.OperationVersion != 1 || !sketchStep.OutputEntityId.HasValue || sketchStep.Parameters == null)
                throw new InvalidOperationException("Only the exact second operation-version-1 CreateSketch output can be dispatched by this boundary.");

            var attempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, stepKey, cancellationToken).ConfigureAwait(false);
            if (attempt == null || attempt.Id != attemptId || attempt.JobId != jobId || attempt.RevisionId != revisionId ||
                attempt.StepKey != stepKey || attempt.Status != CadV2MutationAttemptStatus.Prepared ||
                attempt.OutputEntityId != sketchStep.OutputEntityId ||
                !string.Equals(attempt.PlanSha256, HashPlan(revision.PlanJson), StringComparison.Ordinal))
                throw new InvalidOperationException("The dispatch does not match the exact Prepared sketch attempt for the current stored plan.");

            var priorAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, newPartStep.StepKey, cancellationToken).ConfigureAwait(false);
            var model = await _repository.GetModelAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false);
            if (priorAttempt == null || priorAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                priorAttempt.ModelId != attempt.ModelId ||
                !string.Equals(priorAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                priorAttempt.ProspectiveModelRevisionId == attempt.ProspectiveModelRevisionId ||
                model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                model.CurrentModelRevisionId != priorAttempt.ProspectiveModelRevisionId ||
                await _repository.GetV2ModelOwnerAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false) != jobId)
                throw new InvalidOperationException("The applied NewPart must own the ActiveUnsaved model at the pre-sketch revision.");

            var binding = await _repository.GetEntityBindingAsync(
                attempt.ModelId, attempt.OutputEntityId.Value, model.ConfigurationKey, cancellationToken).ConfigureAwait(false);
            if (binding == null || binding.Status != CadEntityReferenceStatus.Pending || binding.NativeReferenceBytes != null ||
                binding.EntityKind != "Sketch" || binding.NativeObjectKind != "SketchFeature" || binding.ReferenceFormatVersion != 3 ||
                binding.CreatedAtModelRevisionId != attempt.ProspectiveModelRevisionId)
                throw new InvalidOperationException("The exact sketch output must have its reserved pending Bridge reference binding before dispatch.");

            return new CadVersionedCommandRequest(2, sketchStep.OperationVersion.Value, new CadCommandEnvelope
            {
                Command = sketchStep.Command,
                Parameters = (JObject)sketchStep.Parameters.DeepClone(),
                ExecutionId = jobId,
                ManagedModelId = attempt.ModelId,
                OutputEntityId = attempt.OutputEntityId
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
