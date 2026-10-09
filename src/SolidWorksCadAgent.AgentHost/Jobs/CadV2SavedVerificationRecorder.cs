using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Jobs;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>Records one post-reopen check for the exact saved v2 rectangle-boss artifact.</summary>
    public sealed class CadV2SavedVerificationRecorder
    {
        public const string CheckName = "V2SavedRectangleBoss";
        private readonly SqliteJobRepository _repository;
        private readonly CadV2RectangleBossVerifier _verifier;
        private readonly ICadCommandExecutor _executor;
        private readonly WorkspacePolicy _workspace;

        public CadV2SavedVerificationRecorder(SqliteJobRepository repository, ICadCommandExecutor executor,
            WorkspacePolicy workspace)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _verifier = new CadV2RectangleBossVerifier(repository, executor);
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public async Task<bool> VerifyAndRecordAsync(Guid jobId, Guid revisionId, Guid modelId, CancellationToken token)
        {
            var snapshot = await _repository.GetSnapshotAsync(jobId, token).ConfigureAwait(false);
            var revision = snapshot?.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            var model = await _repository.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (snapshot?.Job == null || snapshot.Job.State != JobState.Executing || snapshot.Job.IsSimulated ||
                revision == null || revision.Id != revisionId || model == null ||
                model.Status != CadModelIdentityStatus.ActiveSaved)
                throw new InvalidOperationException("The saved verification context changed before evidence could be recorded.");
            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2?.Steps.Count != 6)
                throw new InvalidOperationException("The saved verification requires the current six-step v2 plan.");

            string plannedPath = null;
            string beforeOpenHash = null;
            string preflightFailure = null;
            try
            {
                plannedPath = _workspace.ResolveForRead(parsed.CandidateV2.Steps[5].Parameters.Value<string>("path"));
                if (!string.Equals(plannedPath, model.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                    preflightFailure = "The registered saved path differs from the approved plan.";
                else
                    beforeOpenHash = HashFile(plannedPath);
            }
            catch (Exception ex) when (ex is WorkspacePolicyException || ex is IOException || ex is UnauthorizedAccessException)
            { preflightFailure = "The saved artifact is unavailable before managed reopen: " + ex.Message; }

            string stagingCleanupWarning = null;
            CadV2RectangleBossVerification verified;
            if (preflightFailure != null)
                verified = CadV2RectangleBossVerification.Fail(preflightFailure);
            else if (!string.Equals(beforeOpenHash, model.LastSavedSha256, StringComparison.Ordinal))
                verified = CadV2RectangleBossVerification.Fail("The saved artifact hash changed before managed reopen.");
            else
            {
                var closed = await _executor.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.CloseDocument, Parameters = new JObject(),
                    ExecutionId = jobId, ManagedModelId = modelId
                }, token).ConfigureAwait(false);
                if (closed == null || !closed.Success || closed.Error != null ||
                    closed.Data?["closed"]?.Type != JTokenType.Boolean || !closed.Data.Value<bool>("closed"))
                    verified = CadV2RectangleBossVerification.Fail("The owned saved document could not be closed for readback.");
                else
                {
                    try
                    {
                        var stage = _workspace.ResolveForRead(CadV2SavePath.StageFor(plannedPath, modelId));
                        File.Delete(stage);
                        if (File.Exists(stage)) stagingCleanupWarning = "The owned staging part remains after close.";
                    }
                    catch (Exception ex) when (ex is WorkspacePolicyException || ex is IOException || ex is UnauthorizedAccessException)
                    { stagingCleanupWarning = "The owned staging part could not be cleaned after close: " + ex.Message; }
                    var reopened = await _executor.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.OpenPart,
                        Parameters = JObject.FromObject(new { path = plannedPath }), ExecutionId = jobId
                    }, token).ConfigureAwait(false);
                    if (reopened == null || !reopened.Success || reopened.Error != null ||
                        reopened.Data?["path"]?.Type != JTokenType.String ||
                        !string.Equals(reopened.Data.Value<string>("path"), plannedPath, StringComparison.OrdinalIgnoreCase) ||
                        reopened.Data["errors"]?.Type != JTokenType.Integer || reopened.Data.Value<int>("errors") != 0)
                        verified = CadV2RectangleBossVerification.Fail("The exact managed saved part could not be reopened.");
                    else
                        verified = await _verifier.VerifySavedAsync(jobId, revisionId, modelId, _workspace, token).ConfigureAwait(false);
                }
            }

            string currentHash = null;
            try
            {
                var path = _workspace.ResolveForRead(parsed.CandidateV2.Steps[5].Parameters.Value<string>("path"));
                if (string.Equals(path, model.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    currentHash = HashFile(path);
                }
            }
            catch (Exception ex) when (ex is WorkspacePolicyException || ex is IOException || ex is UnauthorizedAccessException) { }
            var passed = verified.Passed && string.Equals(currentHash, model.LastSavedSha256, StringComparison.Ordinal);
            var planHash = HashPlan(revision.PlanJson);
            var expected = new JObject
            {
                ["planSha256"] = planHash,
                ["modelId"] = modelId.ToString("D"),
                ["modelRevisionId"] = model.CurrentModelRevisionId.ToString("D"),
                ["path"] = model.CanonicalPath,
                ["sha256"] = model.LastSavedSha256,
                ["widthMm"] = parsed.CandidateV2.Steps[2].Parameters.Value<double>("widthMm"),
                ["heightMm"] = parsed.CandidateV2.Steps[2].Parameters.Value<double>("heightMm"),
                ["depthMm"] = parsed.CandidateV2.Steps[4].Parameters.Value<double>("depthMm"),
                ["volumeMm3"] = parsed.CandidateV2.Steps[2].Parameters.Value<double>("widthMm") *
                    parsed.CandidateV2.Steps[2].Parameters.Value<double>("heightMm") *
                    parsed.CandidateV2.Steps[4].Parameters.Value<double>("depthMm"),
                ["bodyCount"] = 1,
                ["hasRebuildErrors"] = false
            };
            var actual = new JObject
            {
                ["sha256"] = currentHash == null ? JValue.CreateNull() : new JValue(currentHash),
                ["reason"] = passed ? JValue.CreateNull() : new JValue(verified.Reason ?? "The artifact changed after geometry observation."),
                ["stagingCleanupWarning"] = stagingCleanupWarning == null ? JValue.CreateNull() : new JValue(stagingCleanupWarning),
                ["observed"] = verified.Observed?.DeepClone() ?? JValue.CreateNull()
            };
            await _repository.AppendVerificationAsync(new VerificationResultRecord
            {
                Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = revision.RevisionNumber,
                CheckName = CheckName, Passed = passed,
                ExpectedJson = expected.ToString(Formatting.None),
                ActualJson = actual.ToString(Formatting.None), CreatedUtc = DateTime.UtcNow
            }, token).ConfigureAwait(false);
            return passed;
        }

        private static string HashPlan(string json)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
