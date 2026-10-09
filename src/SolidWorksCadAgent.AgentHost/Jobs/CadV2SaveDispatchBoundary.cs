using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Jobs;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>
    /// Dispatches only the final non-overwriting save after narrow geometry verification.
    /// This boundary is isolated; JobCoordinator does not yet approve or execute v2 plans.
    /// </summary>
    public sealed class CadV2SaveDispatchBoundary
    {
        private readonly SqliteJobRepository _repository;
        private readonly IVersionedCadCommandExecutor _executor;
        private readonly ICadCommandExecutor _inspector;
        private readonly WorkspacePolicy _workspace;
        private static readonly ConcurrentDictionary<Guid, byte> GuardedAttempts = new ConcurrentDictionary<Guid, byte>();

        public CadV2SaveDispatchBoundary(SqliteJobRepository repository, IVersionedCadCommandExecutor executor,
            WorkspacePolicy workspace)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _inspector = executor as ICadCommandExecutor ?? throw new ArgumentException("Save dispatch requires a CAD inspection executor.", nameof(executor));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public async Task<CadCommandResult> DispatchAndCommitAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken cancellationToken)
        {
            if (!GuardedAttempts.TryAdd(attemptId, 0))
                throw new InvalidOperationException("This v2 Save attempt has an in-progress or unresolved dispatch; replay is blocked.");
            var releaseGuard = true;
            try
            {
                var request = await PrepareRequestAsync(jobId, revisionId, stepKey, attemptId, cancellationToken).ConfigureAwait(false);
                var expectedPath = _workspace.ResolveForWrite(request.Command.Parameters.Value<string>("path"), false);
                var expectedStage = CadV2SavePath.StageFor(expectedPath, request.Command.ManagedModelId.Value);
                _workspace.ResolveForWrite(expectedStage, false);
                var verified = await new CadV2RectangleBossVerifier(_repository, _inspector).VerifyAsync(
                    jobId, revisionId, request.Command.ManagedModelId.Value, cancellationToken).ConfigureAwait(false);
                if (!verified.Passed)
                    throw new InvalidOperationException("The part cannot be saved because geometry verification failed: " + verified.Reason);
                try
                {
                    releaseGuard = false;
                    var observedResult = await _executor.ExecuteVersionedAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!MatchesSaveResult(expectedPath, expectedStage, observedResult) ||
                        !File.Exists(expectedPath) || !File.Exists(expectedStage))
                        throw new InvalidOperationException("The save result does not identify the exact new workspace artifact; its outcome is uncertain.");
                    string sha256;
                    using (var stream = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();

                    await _repository.CommitV2SaveOutcomeAsync(
                        jobId, revisionId, stepKey, request.Command.ManagedModelId.Value,
                        expectedPath, sha256, observedResult, cancellationToken).ConfigureAwait(false);
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
                throw new InvalidOperationException("A real, plan-validated executing job is required for Save dispatch.");
            var revision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (revision == null || revision.Id != revisionId || revision.JobId != jobId)
                throw new InvalidOperationException("The Save dispatch does not target the current immutable job revision.");

            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null || parsed.CandidateV2.Steps.Count != 6)
                throw new InvalidOperationException("A normalized persisted six-step v2 save plan is required.");
            var steps = parsed.CandidateV2.Steps;
            var newPart = steps[0];
            var sketch = steps[1];
            var rectangle = steps[2];
            var exit = steps[3];
            var boss = steps[4];
            var save = steps[5];
            if (!string.Equals(newPart.Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                !string.Equals(sketch.Command, CadCommandNames.CreateSketch, StringComparison.Ordinal) ||
                sketch.OperationVersion != 1 || !sketch.OutputEntityId.HasValue ||
                !string.Equals(rectangle.Command, CadCommandNames.AddRectangle, StringComparison.Ordinal) ||
                rectangle.OperationVersion != 1 || rectangle.OutputEntityId.HasValue ||
                !string.Equals(exit.Command, CadCommandNames.ExitSketch, StringComparison.Ordinal) ||
                exit.OperationVersion != 1 || exit.OutputEntityId.HasValue ||
                !string.Equals(boss.Command, CadCommandNames.Extrude, StringComparison.Ordinal) ||
                boss.OperationVersion != 2 || boss.OutputEntityId.HasValue ||
                boss.Inputs?.ProfileSketch?.EntityId != sketch.OutputEntityId ||
                !string.Equals(save.StepKey, stepKey, StringComparison.Ordinal) ||
                !string.Equals(save.Command, CadCommandNames.SavePart, StringComparison.Ordinal) ||
                save.OperationVersion != 1 || save.OutputEntityId.HasValue || save.Parameters == null ||
                string.IsNullOrWhiteSpace(save.Parameters.Value<string>("path")) ||
                save.Parameters.Value<bool?>("allowOverwrite") == true)
                throw new InvalidOperationException("Only the exact sixth non-overwriting SavePart may be dispatched.");

            var attempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, stepKey, cancellationToken).ConfigureAwait(false);
            if (attempt == null || attempt.Id != attemptId || attempt.JobId != jobId || attempt.RevisionId != revisionId ||
                attempt.StepKey != stepKey || attempt.Status != CadV2MutationAttemptStatus.Prepared || attempt.OutputEntityId.HasValue ||
                !string.Equals(attempt.PlanSha256, HashPlan(revision.PlanJson), StringComparison.Ordinal))
                throw new InvalidOperationException("The Save dispatch does not match its exact Prepared attempt and stored plan.");

            var sketchAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, sketch.StepKey, cancellationToken).ConfigureAwait(false);
            var rectangleAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, rectangle.StepKey, cancellationToken).ConfigureAwait(false);
            var exitAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, exit.StepKey, cancellationToken).ConfigureAwait(false);
            var bossAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, boss.StepKey, cancellationToken).ConfigureAwait(false);
            var newPartAttempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, newPart.StepKey, cancellationToken).ConfigureAwait(false);
            var model = await _repository.GetModelAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false);
            if (sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                sketchAttempt.ModelId != attempt.ModelId || sketchAttempt.OutputEntityId != sketch.OutputEntityId ||
                !string.Equals(sketchAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                rectangleAttempt == null || rectangleAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                rectangleAttempt.ModelId != attempt.ModelId || rectangleAttempt.OutputEntityId.HasValue ||
                !string.Equals(rectangleAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                exitAttempt == null || exitAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                exitAttempt.ModelId != attempt.ModelId || exitAttempt.OutputEntityId.HasValue ||
                !string.Equals(exitAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                bossAttempt == null || bossAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                bossAttempt.ModelId != attempt.ModelId || bossAttempt.OutputEntityId.HasValue ||
                !string.Equals(bossAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                newPartAttempt.ModelId != attempt.ModelId || newPartAttempt.OutputEntityId.HasValue ||
                !string.Equals(newPartAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                !string.Equals(model.DocumentKind, "Part", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256) ||
                model.CurrentModelRevisionId != bossAttempt.ProspectiveModelRevisionId ||
                await _repository.GetV2ModelOwnerAsync(attempt.ModelId, cancellationToken).ConfigureAwait(false) != jobId)
                throw new InvalidOperationException("The Applied boss and owned ActiveUnsaved model do not match the pre-save revision.");

            var binding = await _repository.GetEntityBindingAsync(
                attempt.ModelId, sketch.OutputEntityId.Value, model.ConfigurationKey, cancellationToken).ConfigureAwait(false);
            if (binding == null || binding.Status != CadEntityReferenceStatus.Active ||
                binding.EntityKind != "Sketch" || binding.NativeObjectKind != "SketchFeature" ||
                binding.ReferenceFormatVersion != 3 || binding.CreatedAtModelRevisionId != sketchAttempt.ProspectiveModelRevisionId ||
                binding.NativeReferenceBytes == null || binding.NativeReferenceBytes.Length == 0 ||
                binding.NativeReferenceBytes.Length > 1024 * 1024)
                throw new InvalidOperationException("The exact preceding sketch must have its committed bounded native reference.");

            return new CadVersionedCommandRequest(2, 1, new CadCommandEnvelope
            {
                Command = CadCommandNames.SavePart,
                Parameters = (JObject)save.Parameters.DeepClone(),
                ExecutionId = jobId,
                ManagedModelId = attempt.ModelId
            });
        }

        private static bool MatchesSaveResult(string expectedPath, string expectedStage, CadCommandResult result)
        {
            if (result == null || !result.Success || result.Error != null || result.Data == null) return false;
            return result.Data["path"]?.Type == JTokenType.String &&
                string.Equals(result.Data.Value<string>("path"), expectedPath, StringComparison.OrdinalIgnoreCase) &&
                result.Data["stagingPath"]?.Type == JTokenType.String &&
                string.Equals(result.Data.Value<string>("stagingPath"), expectedStage, StringComparison.OrdinalIgnoreCase) &&
                result.Data["errors"]?.Type == JTokenType.Integer && result.Data.Value<int>("errors") == 0;
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
