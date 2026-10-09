using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>Runs only the first approved, owned six-step v2 part through the durable mutation boundaries.</summary>
    public sealed class CadV2SixStepRunner
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;
        private readonly WorkspacePolicy _workspace;

        public CadV2SixStepRunner(SqliteJobRepository repository, ICadCommandExecutor executor, WorkspacePolicy workspace)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor;
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public async Task<bool> RunAsync(Guid jobId, Guid revisionId, CancellationToken token,
            string approvedPlanSha256 = null)
        {
            var snapshot = await _repository.GetSnapshotAsync(jobId, token).ConfigureAwait(false);
            var revision = snapshot?.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (snapshot?.Job == null || revision == null || revision.Id != revisionId ||
                snapshot.Job.State != JobState.Executing || !snapshot.Job.PlanValidated || snapshot.Job.IsSimulated)
                throw new InvalidOperationException("A current, real, validated executing v2 revision is required.");
            if (approvedPlanSha256 != null && !string.Equals(approvedPlanSha256, HashPlan(revision.PlanJson), StringComparison.Ordinal))
                throw new InvalidOperationException("The approved version-2 plan changed before native dispatch.");

            var modelId = Guid.NewGuid();
            var readiness = CadPlanV2ExecutionReadiness.Assess(revision.PlanJson, ExecutionMode.Real,
                _executor, jobId, modelId);
            if (!readiness.IsReady || readiness.Requests.Count != 6)
                throw new InvalidOperationException("The current plan is outside the exact six-step native scope: " +
                    string.Join(" ", readiness.Errors));
            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            var steps = parsed.CandidateV2.Steps;
            var commands = new[] { CadCommandNames.NewPart, CadCommandNames.CreateSketch,
                CadCommandNames.AddRectangle, CadCommandNames.ExitSketch, CadCommandNames.Extrude,
                CadCommandNames.SavePart };
            if (steps.Count != commands.Length || steps.Where((step, index) => step.Command != commands[index]).Any())
                throw new InvalidOperationException("Only the exact six-step rectangle-boss plan can execute.");

            // Resolve both owned outputs before NewPart. The Save boundary repeats this check immediately before dispatch.
            var path = _workspace.ResolveForWrite(steps[5].Parameters.Value<string>("path"), false);
            _workspace.ResolveForWrite(CadV2SavePath.StageFor(path, modelId), false);
            var versioned = _executor as IVersionedCadCommandExecutor;
            if (versioned == null) throw new InvalidOperationException("Versioned native dispatch is unavailable.");
            var now = DateTime.UtcNow;
            var model = new CadModelIdentityRecord
            {
                ModelId = modelId, DocumentKind = "Part", Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId", CurrentModelRevisionId = Guid.NewGuid(),
                ConfigurationKey = "Pending", RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now
            };

            for (var index = 0; index < commands.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var current = await _repository.GetSnapshotAsync(jobId, token).ConfigureAwait(false);
                if (current?.Job == null || current.Job.State != JobState.Executing ||
                    !current.Job.PlanValidated || current.Revisions.LastOrDefault()?.Id != revisionId)
                    throw new InvalidOperationException("The approved job or plan changed before the next native step.");
                if (approvedPlanSha256 != null && !string.Equals(approvedPlanSha256,
                    HashPlan(current.Revisions.Last().PlanJson), StringComparison.Ordinal))
                    throw new InvalidOperationException("The approved version-2 plan changed before the next native step.");
                var step = steps[index];
                CadV2MutationAttemptRecord attempt;
                switch (index)
                {
                    case 0: attempt = await _repository.PrepareV2NewPartAttemptAsync(jobId, revisionId, step.StepKey, model, token).ConfigureAwait(false); break;
                    case 1: attempt = await _repository.PrepareV2SketchAttemptAsync(jobId, revisionId, step.StepKey, modelId, token).ConfigureAwait(false); break;
                    case 2: attempt = await _repository.PrepareV2RectangleAttemptAsync(jobId, revisionId, step.StepKey, modelId, token).ConfigureAwait(false); break;
                    case 3: attempt = await _repository.PrepareV2ExitSketchAttemptAsync(jobId, revisionId, step.StepKey, modelId, token).ConfigureAwait(false); break;
                    case 4: attempt = await _repository.PrepareV2BossAttemptAsync(jobId, revisionId, step.StepKey, modelId, token).ConfigureAwait(false); break;
                    default: attempt = await _repository.PrepareV2SaveAttemptAsync(jobId, revisionId, step.StepKey, modelId, token).ConfigureAwait(false); break;
                }
                var started = DateTime.UtcNow;
                CadCommandResult result;
                try
                {
                    switch (index)
                    {
                        case 0: result = await new CadV2NewPartDispatchBoundary(_repository, versioned).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                        case 1: result = await new CadV2CreateSketchDispatchBoundary(_repository, versioned).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                        case 2: result = await new CadV2RectangleDispatchBoundary(_repository, versioned).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                        case 3: result = await new CadV2ExitSketchDispatchBoundary(_repository, versioned).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                        case 4: result = await new CadV2BossDispatchBoundary(_repository, versioned).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                        default: result = await new CadV2SaveDispatchBoundary(_repository, versioned, _workspace).DispatchAndCommitAsync(jobId, revisionId, step.StepKey, attempt.Id, token).ConfigureAwait(false); break;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        await _repository.AppendCommandAsync(new CommandExecutionRecord
                        {
                            Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = revision.RevisionNumber,
                            SequenceNumber = index + 1, CommandName = step.Command,
                            ParametersJson = (step.Parameters ?? new JObject()).ToString(Formatting.None),
                            Success = false, ResultJson = "{}", ErrorCode = "V2_STEP_FAILED",
                            ErrorMessage = ex.Message, StartedUtc = started, CompletedUtc = DateTime.UtcNow
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception persistenceFailure) { ex.Data["CadV2CommandHistoryFailure"] = persistenceFailure; }
                    throw;
                }
                if (result == null || !result.Success || result.Error != null)
                    throw new InvalidOperationException("The v2 mutation boundary did not commit a successful result.");
                await _repository.AppendCommandAsync(new CommandExecutionRecord
                {
                    Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = revision.RevisionNumber,
                    SequenceNumber = index + 1, CommandName = step.Command,
                    ParametersJson = (step.Parameters ?? new JObject()).ToString(Formatting.None),
                    Success = true, ResultJson = (result.Data ?? new JObject()).ToString(Formatting.None),
                    StartedUtc = started, CompletedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);
            }

            return await new CadV2SavedVerificationRecorder(_repository, _executor, _workspace)
                .VerifyAndRecordAsync(jobId, revisionId, modelId, token).ConfigureAwait(false);
        }

        private static string HashPlan(string json)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
