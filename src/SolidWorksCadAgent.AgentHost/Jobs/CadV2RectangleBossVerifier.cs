using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>
    /// Observes only the first Top Plane centre-rectangle boss after its exact Host commit,
    /// either before save or after managed reopen. The result is not approval or a durable record.
    /// </summary>
    public sealed class CadV2RectangleBossVerifier
    {
        private const double ToleranceMm = 0.02;
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;

        public CadV2RectangleBossVerifier(SqliteJobRepository repository, ICadCommandExecutor executor)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public async Task<CadV2RectangleBossVerification> VerifyAsync(
            Guid jobId, Guid revisionId, Guid modelId, CancellationToken token)
            => await VerifyCoreAsync(jobId, revisionId, modelId, null, token).ConfigureAwait(false);

        public Task<CadV2RectangleBossVerification> VerifySavedAsync(
            Guid jobId, Guid revisionId, Guid modelId, WorkspacePolicy workspace, CancellationToken token)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            return VerifyCoreAsync(jobId, revisionId, modelId, workspace, token);
        }

        private async Task<CadV2RectangleBossVerification> VerifyCoreAsync(
            Guid jobId, Guid revisionId, Guid modelId, WorkspacePolicy savedWorkspace, CancellationToken token)
        {
            if (jobId == Guid.Empty || revisionId == Guid.Empty || modelId == Guid.Empty)
                throw new ArgumentException("Job, revision and model identities are required.");
            var snapshot = await _repository.GetSnapshotAsync(jobId, token).ConfigureAwait(false);
            if (snapshot?.Job == null || snapshot.Job.IsSimulated || !snapshot.Job.PlanValidated ||
                snapshot.Job.State != JobState.Executing)
                throw new InvalidOperationException("Only a current real executing validated v2 job can be verified.");
            var revision = snapshot.Revisions.OrderBy(item => item.RevisionNumber).LastOrDefault();
            if (revision == null || revision.Id != revisionId || revision.JobId != jobId)
                throw new InvalidOperationException("Verification requires the current immutable job revision.");
            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null ||
                parsed.CandidateV2.Steps.Count < 5 || parsed.CandidateV2.Steps.Count > 6)
                throw new InvalidOperationException("Only the normalized narrow rectangle-boss plan can be verified.");
            var steps = parsed.CandidateV2.Steps;
            var sketch = steps[1]; var rectangle = steps[2]; var boss = steps[4];
            if (steps[0].Command != CadCommandNames.NewPart || sketch.Command != CadCommandNames.CreateSketch ||
                sketch.Parameters.Value<string>("plane") != "Top Plane" || !sketch.OutputEntityId.HasValue ||
                rectangle.Command != CadCommandNames.AddRectangle || steps[3].Command != CadCommandNames.ExitSketch ||
                boss.Command != CadCommandNames.Extrude || boss.OperationVersion != 2 ||
                boss.Inputs?.ProfileSketch?.EntityId != sketch.OutputEntityId ||
                steps.Count == 6 && steps[5].Command != CadCommandNames.SavePart)
                throw new InvalidOperationException("The plan is outside the first Top Plane rectangle-boss verification scope.");
            var width = rectangle.Parameters.Value<double>("widthMm");
            var height = rectangle.Parameters.Value<double>("heightMm");
            var depth = boss.Parameters.Value<double>("depthMm");

            var attempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, boss.StepKey, token).ConfigureAwait(false);
            var model = await _repository.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Applied || attempt.ModelId != modelId ||
                attempt.OutputEntityId.HasValue || attempt.PlanSha256 != HashPlan(revision.PlanJson) ||
                model == null || model.DocumentKind != "Part" ||
                await _repository.GetV2ModelOwnerAsync(modelId, token).ConfigureAwait(false) != jobId)
                throw new InvalidOperationException("The owned part is not at its Applied boss revision.");

            Guid expectedRevision;
            if (savedWorkspace == null)
            {
                if (model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                    model.CurrentModelRevisionId != attempt.ProspectiveModelRevisionId ||
                    !string.IsNullOrWhiteSpace(model.CanonicalPath))
                    throw new InvalidOperationException("The owned unsaved part is not at its Applied boss revision.");
                expectedRevision = attempt.ProspectiveModelRevisionId;
            }
            else
            {
                if (steps.Count != 6 || model.Status != CadModelIdentityStatus.ActiveSaved ||
                    string.IsNullOrWhiteSpace(model.CanonicalPath) || string.IsNullOrWhiteSpace(model.LastSavedSha256))
                    throw new InvalidOperationException("An exact saved six-step owned part is required.");
                var save = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, steps[5].StepKey, token).ConfigureAwait(false);
                if (save == null || save.Status != CadV2MutationAttemptStatus.Applied || save.ModelId != modelId ||
                    save.PlanSha256 != attempt.PlanSha256 || save.OutputEntityId.HasValue ||
                    model.CurrentModelRevisionId != save.ProspectiveModelRevisionId)
                    throw new InvalidOperationException("The final save has no matching Applied attempt and state revision.");
                string path;
                try { path = savedWorkspace.ResolveForRead(steps[5].Parameters.Value<string>("path")); }
                catch (Exception ex) when (ex is WorkspacePolicyException || ex is IOException || ex is UnauthorizedAccessException)
                { return CadV2RectangleBossVerification.Fail("The saved artifact is unavailable inside the approved workspace."); }
                if (!string.Equals(path, model.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                    return CadV2RectangleBossVerification.Fail("The registered saved path differs from the approved plan.");
                string hash;
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { return CadV2RectangleBossVerification.Fail("The saved artifact could not be hashed."); }
                if (!string.Equals(hash, model.LastSavedSha256, StringComparison.Ordinal))
                    return CadV2RectangleBossVerification.Fail("The saved artifact hash differs from the committed identity.");
                expectedRevision = save.ProspectiveModelRevisionId;
            }

            var rebuild = await ObserveAsync(CadCommandNames.GetRebuildErrors, jobId, modelId, token).ConfigureAwait(false);
            if (rebuild == null || !rebuild.Success || rebuild.Error != null || rebuild.Data?["hasErrors"]?.Type != JTokenType.Boolean ||
                rebuild.Data.Value<bool>("hasErrors"))
                return CadV2RectangleBossVerification.Fail("Rebuild reported errors or no valid error observation.");
            var bodies = await ObserveAsync(CadCommandNames.GetBodyCount, jobId, modelId, token).ConfigureAwait(false);
            if (bodies == null || !bodies.Success || bodies.Error != null || bodies.Data?["bodyCount"]?.Type != JTokenType.Integer ||
                bodies.Data.Value<int>("bodyCount") != 1)
                return CadV2RectangleBossVerification.Fail("The part does not contain exactly one observed solid body.");
            var expectedVolume = width * height * depth;
            if (!ReadFinite(bodies.Data, "volumeMm3", out var actualVolume) ||
                Math.Abs(actualVolume - expectedVolume) > Math.Max(0.05, expectedVolume * 0.0001))
                return CadV2RectangleBossVerification.Fail("The observed solid volume differs from the planned rectangular boss.");
            var bounds = await ObserveAsync(CadCommandNames.GetBoundingBox, jobId, modelId, token).ConfigureAwait(false);
            if (bounds == null || !bounds.Success || bounds.Error != null || !MatchesBounds(bounds.Data, width, height, depth))
                return CadV2RectangleBossVerification.Fail("The observed bounds differ from the planned centred rectangle boss: " +
                    (bounds?.Data?.ToString(Newtonsoft.Json.Formatting.None) ?? bounds?.Error?.Code ?? "no result"));

            var after = await _repository.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (after == null || after.Status != (savedWorkspace == null ? CadModelIdentityStatus.ActiveUnsaved : CadModelIdentityStatus.ActiveSaved) ||
                after.CurrentModelRevisionId != expectedRevision ||
                savedWorkspace != null && (after.CanonicalPath != model.CanonicalPath || after.LastSavedSha256 != model.LastSavedSha256))
                return CadV2RectangleBossVerification.Fail("The managed model changed during geometry observation.");
            return CadV2RectangleBossVerification.Pass(new JObject
            {
                ["rebuild"] = rebuild.Data.DeepClone(),
                ["bodies"] = bodies.Data.DeepClone(),
                ["bounds"] = bounds.Data.DeepClone()
            });
        }

        private Task<CadCommandResult> ObserveAsync(string command, Guid jobId, Guid modelId, CancellationToken token) =>
            _executor.ExecuteAsync(new CadCommandEnvelope
            {
                Command = command, Parameters = new JObject(), ExecutionId = jobId, ManagedModelId = modelId
            }, token);

        private static bool MatchesBounds(JObject data, double width, double height, double depth)
        {
            if (data == null) return false;
            if (!ReadFinite(data, "minXmm", out var minX) || !ReadFinite(data, "maxXmm", out var maxX) ||
                !ReadFinite(data, "minYmm", out var minY) || !ReadFinite(data, "maxYmm", out var maxY) ||
                !ReadFinite(data, "minZmm", out var minZ) || !ReadFinite(data, "maxZmm", out var maxZ) ||
                !ReadFinite(data, "sizeXmm", out var sizeX) || !ReadFinite(data, "sizeYmm", out var sizeY) ||
                !ReadFinite(data, "sizeZmm", out var sizeZ)) return false;
            // SOLIDWORKS Top Plane sketch X maps to model X; sketch Y maps to model Z;
            // the positive sketch normal for this supported part workflow maps to model +Y.
            return Close(sizeX, width) && Close(sizeY, depth) && Close(sizeZ, height) &&
                Close(minX, -width / 2.0) && Close(maxX, width / 2.0) &&
                Close(minY, 0) && Close(maxY, depth) &&
                Close(minZ, -height / 2.0) && Close(maxZ, height / 2.0) &&
                Close(maxX - minX, sizeX) && Close(maxY - minY, sizeY) && Close(maxZ - minZ, sizeZ) &&
                sizeX > 0 && sizeY > 0 && sizeZ > 0;
        }

        private static bool ReadFinite(JObject data, string key, out double value)
        {
            value = 0;
            var token = data[key];
            if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            value = token.Value<double>();
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= ToleranceMm;

        private static string HashPlan(string json)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class CadV2RectangleBossVerification
    {
        private CadV2RectangleBossVerification(bool passed, string reason, JObject observed)
        { Passed = passed; Reason = reason; Observed = observed; }
        public bool Passed { get; }
        public string Reason { get; }
        public JObject Observed { get; }
        public static CadV2RectangleBossVerification Pass(JObject observed) =>
            new CadV2RectangleBossVerification(true, null, observed);
        public static CadV2RectangleBossVerification Fail(string reason) =>
            new CadV2RectangleBossVerification(false, reason, null);
    }
}
