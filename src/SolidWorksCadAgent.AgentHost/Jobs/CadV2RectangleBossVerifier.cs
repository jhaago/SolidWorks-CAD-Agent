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
    /// Observes supported Top Plane prismatic geometry after its exact Host commit,
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
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                throw new InvalidOperationException("Only a normalized v2 prismatic plan can be verified.");
            CadV2PrismaticGeometry geometry;
            try { geometry = CadV2PrismaticGeometry.Analyze(parsed.CandidateV2); }
            catch (ArgumentException ex) { throw new InvalidOperationException("The plan is outside prismatic verification scope.", ex); }
            var steps = parsed.CandidateV2.Steps;
            var planHash = HashPlan(revision.PlanJson);
            var attempt = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, geometry.LastFeatureStepKey, token).ConfigureAwait(false);
            var model = await _repository.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Applied || attempt.ModelId != modelId ||
                attempt.OutputEntityId.HasValue || attempt.PlanSha256 != planHash ||
                model == null || model.DocumentKind != "Part" ||
                await _repository.GetV2ModelOwnerAsync(modelId, token).ConfigureAwait(false) != jobId)
                throw new InvalidOperationException("The owned part is not at its Applied final feature revision.");

            // Geometry observation cannot substitute for committed provenance of earlier mutations.
            foreach (var step in steps)
            {
                if (step == geometry.SaveStep && savedWorkspace == null) break;
                var prior = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, step.StepKey, token).ConfigureAwait(false);
                if (prior == null || prior.Status != CadV2MutationAttemptStatus.Applied || prior.JobId != jobId ||
                    prior.RevisionId != revisionId || prior.ModelId != modelId || prior.PlanSha256 != planHash ||
                    prior.OutputEntityId != step.OutputEntityId)
                    throw new InvalidOperationException("Every planned mutation must have matching Applied provenance before verification.");
            }

            Guid expectedRevision;
            if (savedWorkspace == null)
            {
                if (model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                    model.CurrentModelRevisionId != attempt.ProspectiveModelRevisionId ||
                    !string.IsNullOrWhiteSpace(model.CanonicalPath))
                    throw new InvalidOperationException("The owned unsaved part is not at its Applied final feature revision.");
                expectedRevision = attempt.ProspectiveModelRevisionId;
            }
            else
            {
                if (geometry.SaveStep == null || model.Status != CadModelIdentityStatus.ActiveSaved ||
                    string.IsNullOrWhiteSpace(model.CanonicalPath) || string.IsNullOrWhiteSpace(model.LastSavedSha256))
                    throw new InvalidOperationException("An exact saved owned prismatic part is required.");
                var save = await _repository.FindV2MutationAttemptAsync(jobId, revisionId, geometry.SaveStep.StepKey, token).ConfigureAwait(false);
                if (save == null || save.Status != CadV2MutationAttemptStatus.Applied || save.ModelId != modelId ||
                    save.PlanSha256 != attempt.PlanSha256 || save.OutputEntityId.HasValue ||
                    model.CurrentModelRevisionId != save.ProspectiveModelRevisionId)
                    throw new InvalidOperationException("The final save has no matching Applied attempt and state revision.");
                string path;
                try { path = savedWorkspace.ResolveForRead(geometry.SaveStep.Parameters.Value<string>("path")); }
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
            var expectedVolume = geometry.VolumeMm3;
            var bounds = await ObserveAsync(CadCommandNames.GetBoundingBox, jobId, modelId, token).ConfigureAwait(false);
            if (bounds == null || !bounds.Success || bounds.Error != null || bounds.Data == null)
                return CadV2RectangleBossVerification.Fail("The part has no valid observed bounding box.");
            if (!ReadFinite(bodies.Data, "volumeMm3", out var actualVolume) ||
                Math.Abs(actualVolume - expectedVolume) > Math.Max(0.05, expectedVolume * 0.0001))
                return CadV2RectangleBossVerification.Fail("The observed solid volume " + actualVolume.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " mm3 differs from planned " + expectedVolume.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " mm3; observed bounds " + bounds.Data.ToString(Newtonsoft.Json.Formatting.None) + ".");
            if (!MatchesBounds(bounds.Data, geometry))
                return CadV2RectangleBossVerification.Fail("The observed bounds differ from the planned prismatic part: " +
                    (bounds?.Data?.ToString(Newtonsoft.Json.Formatting.None) ?? bounds?.Error?.Code ?? "no result"));
            var nativeCuts = await ObserveAsync(CadCommandNames.GetPrismaticCutReadback, jobId, modelId, token).ConfigureAwait(false);
            var cutArray = nativeCuts?.Data?["cuts"] as JArray;
            if (nativeCuts == null || !nativeCuts.Success || nativeCuts.Error != null || cutArray == null)
                return CadV2RectangleBossVerification.Fail("Native cut definitions and parent sketches could not be read back" +
                    (nativeCuts?.Error == null ? "." : " (" + nativeCuts.Error.Code + "): " + nativeCuts.Error.Message +
                        (string.IsNullOrWhiteSpace(nativeCuts.Error.Detail) ? string.Empty : " " + nativeCuts.Error.Detail)));
            if (!CadV2NativeCutReadbackMatcher.Matches(geometry.Cuts, cutArray, out var cutMismatch))
                return CadV2RectangleBossVerification.Fail(cutMismatch);

            var after = await _repository.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (after == null || after.Status != (savedWorkspace == null ? CadModelIdentityStatus.ActiveUnsaved : CadModelIdentityStatus.ActiveSaved) ||
                after.CurrentModelRevisionId != expectedRevision ||
                savedWorkspace != null && (after.CanonicalPath != model.CanonicalPath || after.LastSavedSha256 != model.LastSavedSha256))
                return CadV2RectangleBossVerification.Fail("The managed model changed during geometry observation.");
            JObject parameterObservation = null;
            if (CadParametricPlateContract.IsParametric(parsed.CandidateV2))
            {
                var parameters = await new CadParametricPlateVerifier(_repository, _executor).VerifyAsync(jobId, revisionId, modelId, token).ConfigureAwait(false);
                if (!parameters.Passed) return parameters;
                parameterObservation = parameters.Observed;
            }
            JObject holeObservation = null;
            if (CadParametricHoleContract.IsHole(parsed.CandidateV2))
            {
                var parameters = await new CadParametricHoleVerifier(_repository, _executor).VerifyAsync(jobId, revisionId, modelId, token).ConfigureAwait(false);
                if (!parameters.Passed) return parameters;
                holeObservation = parameters.Observed;
            }
            return CadV2RectangleBossVerification.Pass(new JObject
            {
                ["rebuild"] = rebuild.Data.DeepClone(),
                ["bodies"] = bodies.Data.DeepClone(),
                ["bounds"] = bounds.Data.DeepClone(),
                ["nativeCuts"] = cutArray.DeepClone(),
                ["parametricPlate"] = parameterObservation?["parametricPlate"]?.DeepClone(),
                ["parametricHole"] = holeObservation?["parametricHole"]?.DeepClone()
            });
        }

        private Task<CadCommandResult> ObserveAsync(string command, Guid jobId, Guid modelId, CancellationToken token) =>
            _executor.ExecuteAsync(new CadCommandEnvelope
            {
                Command = command, Parameters = new JObject(), ExecutionId = jobId, ManagedModelId = modelId
            }, token);

        private static bool MatchesBounds(JObject data, CadV2PrismaticGeometry geometry)
        {
            if (data == null) return false;
            foreach (var expected in geometry.ExpectedBounds().Properties())
                if (!ReadFinite(data, expected.Name, out var actual) || !Close(actual, expected.Value.Value<double>()))
                    return false;
            if (!ReadFinite(data, "minXmm", out var minX) || !ReadFinite(data, "maxXmm", out var maxX) ||
                !ReadFinite(data, "minYmm", out var minY) || !ReadFinite(data, "maxYmm", out var maxY) ||
                !ReadFinite(data, "minZmm", out var minZ) || !ReadFinite(data, "maxZmm", out var maxZ) ||
                !ReadFinite(data, "sizeXmm", out var sizeX) || !ReadFinite(data, "sizeYmm", out var sizeY) ||
                !ReadFinite(data, "sizeZmm", out var sizeZ)) return false;
            return Close(maxX - minX, sizeX) && Close(maxY - minY, sizeY) && Close(maxZ - minZ, sizeZ) &&
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
