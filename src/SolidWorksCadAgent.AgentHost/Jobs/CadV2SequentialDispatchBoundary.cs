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
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    // All old and new entry points share this fence. A failed durable uncertainty
    // write retains it until process restart; Prepared recovery then blocks replay.
    internal static class CadV2DispatchFence
    {
        internal static readonly ConcurrentDictionary<Guid, byte> Attempts = new ConcurrentDictionary<Guid, byte>();
    }

    public sealed class CadV2SequentialDispatchBoundary
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;
        private readonly IVersionedCadCommandExecutor _versioned;
        private readonly WorkspacePolicy _workspace;
        public CadV2SequentialDispatchBoundary(SqliteJobRepository repository, ICadCommandExecutor executor, WorkspacePolicy workspace)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _versioned = executor as IVersionedCadCommandExecutor ?? throw new ArgumentException("Versioned dispatch is required.", nameof(executor));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public async Task<CadCommandResult> DispatchAndCommitAsync(Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken token)
        {
            if (!CadV2DispatchFence.Attempts.TryAdd(attemptId, 0))
                throw new InvalidOperationException("This attempt has an in-progress or unresolved dispatch; replay is blocked.");
            bool release = true;
            try
            {
                var attempt = await _repository.ValidateV2SequentialAttemptAsync(jobId, revisionId, stepKey, attemptId, token).ConfigureAwait(false);
                var snapshot = await _repository.GetSnapshotAsync(jobId, token).ConfigureAwait(false);
                var revision = snapshot?.Revisions.OrderBy(r => r.RevisionNumber).LastOrDefault();
                if (revision == null || revision.Id != revisionId || Hash(Encoding.UTF8.GetBytes(revision.PlanJson)) != attempt.PlanSha256)
                    throw new InvalidOperationException("The stored plan changed before binding.");
                var readiness = CadPlanV2ExecutionReadiness.Assess(revision.PlanJson, ExecutionMode.Real, _executor, jobId, attempt.ModelId);
                if (!readiness.IsReady) throw new InvalidOperationException(string.Join(" ", readiness.Errors));
                var plan = CadPlanDocumentReader.ReadPersisted(revision.PlanJson).CandidateV2;
                var index = plan.Steps.FindIndex(s => s.StepKey == stepKey);
                if (index <= 0) throw new InvalidOperationException("Only a post-NewPart step can use this boundary.");
                var request = readiness.Requests[index];
                string path = null, stage = null;
                if (request.Command.Command == CadCommandNames.SavePart)
                {
                    path = _workspace.ResolveForWrite(request.Command.Parameters.Value<string>("path"), false);
                    stage = _workspace.ResolveForWrite(CadV2SavePath.StageFor(path, attempt.ModelId), false);
                    var verified = await new CadV2RectangleBossVerifier(_repository, _executor)
                        .VerifyAsync(jobId, revisionId, attempt.ModelId, token).ConfigureAwait(false);
                    if (!verified.Passed) throw new InvalidOperationException("Pre-save geometry verification failed: " + verified.Reason);
                }
                // Repeat provenance after inspection and immediately before executor entry.
                await _repository.ValidateV2SequentialAttemptAsync(jobId, revisionId, stepKey, attemptId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                try
                {
                    release = false;
                    var result = await _versioned.ExecuteVersionedAsync(request, token).ConfigureAwait(false);
                    if ((request.Command.Command == CadCommandNames.AddRectangle && request.OperationVersion == 2) ||
                        (request.Command.Command == CadCommandNames.Extrude && request.OperationVersion == 3))
                        await _repository.CommitV2ParametricOutcomeAsync(jobId, revisionId, stepKey, attempt.ModelId, result, token).ConfigureAwait(false);
                    else if (path == null)
                        await _repository.CommitV2SequentialOutcomeAsync(jobId, revisionId, stepKey, attempt.ModelId, result, token).ConfigureAwait(false);
                    else
                    {
                        if (result == null || !result.Success || result.Error != null ||
                            result.Data?["path"]?.Type != JTokenType.String ||
                            !string.Equals(result.Data.Value<string>("path"), path, StringComparison.OrdinalIgnoreCase) ||
                            result.Data["stagingPath"]?.Type != JTokenType.String ||
                            !string.Equals(result.Data.Value<string>("stagingPath"), stage, StringComparison.OrdinalIgnoreCase) ||
                            result.Data["errors"]?.Type != JTokenType.Integer || result.Data.Value<int>("errors") != 0 ||
                            !File.Exists(path) || !File.Exists(stage))
                            throw new InvalidOperationException("Save did not identify the exact new staged artifact; its outcome is uncertain.");
                        string sha;
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var algorithm = SHA256.Create())
                            sha = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                        await _repository.CommitV2SequentialSaveOutcomeAsync(jobId, revisionId, stepKey, attempt.ModelId, path, sha, result, token).ConfigureAwait(false);
                    }
                    release = true;
                    return result;
                }
                catch (Exception failure)
                {
                    try
                    {
                        await _repository.MarkV2MutationAttemptUncertainAsync(attemptId, CancellationToken.None).ConfigureAwait(false);
                        release = true;
                    }
                    catch (Exception persistenceFailure) { failure.Data["CadV2UncertainPersistenceFailure"] = persistenceFailure; }
                    throw;
                }
            }
            finally { if (release) CadV2DispatchFence.Attempts.TryRemove(attemptId, out _); }
        }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
