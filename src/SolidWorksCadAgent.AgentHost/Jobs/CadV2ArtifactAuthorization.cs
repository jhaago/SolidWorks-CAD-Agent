using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>Fail-closed delivery check for a saved v2 prismatic artifact.</summary>
    public static class CadV2ArtifactAuthorization
    {
        public static bool MatchesBytes(byte[] bytes, string expectedSha256)
        {
            if (bytes == null || string.IsNullOrWhiteSpace(expectedSha256)) return false;
            using (var sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(),
                    expectedSha256, StringComparison.Ordinal);
        }

        public static bool IsAuthorized(JobSnapshot snapshot, JobRevision revision, CommandExecutionRecord save,
            CadV2MutationAttemptRecord saveAttempt, CadModelIdentityRecord model, Guid? ownerJobId,
            string canonicalPath, WorkspacePolicy workspace)
        {
            if (snapshot?.Job == null || revision == null || save == null || saveAttempt == null ||
                model == null || workspace == null || snapshot.Job.IsSimulated || !snapshot.Job.PlanValidated ||
                (snapshot.Job.State != JobState.ReadyForReview && snapshot.Job.State != JobState.Completed) ||
                ownerJobId != snapshot.Job.Id || model.Status != CadModelIdentityStatus.ActiveSaved ||
                saveAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                saveAttempt.JobId != snapshot.Job.Id || saveAttempt.RevisionId != revision.Id ||
                saveAttempt.ModelId != model.ModelId ||
                saveAttempt.OutputEntityId.HasValue || model.DocumentKind != "Part" ||
                saveAttempt.ProspectiveModelRevisionId != model.CurrentModelRevisionId ||
                save.RevisionNumber != revision.RevisionNumber || !save.Success ||
                !string.Equals(save.CommandName, CadCommandNames.SavePart, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(model.LastSavedSha256) || model.LastSavedSha256.Length != 64)
                return false;

            var parsed = CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                return false;

            try
            {
                var geometry = CadV2PrismaticGeometry.Analyze(parsed.CandidateV2);
                if (geometry.SaveStep == null || saveAttempt.StepKey != geometry.SaveStep.StepKey) return false;
                var plannedPath = workspace.ResolveForRead(geometry.SaveStep.Parameters.Value<string>("path"));
                var recordedPath = workspace.ResolveForRead(JObject.Parse(save.ResultJson).Value<string>("path"));
                if (!string.Equals(plannedPath, canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(recordedPath, canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(model.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    return false;
                using (var sha = SHA256.Create())
                {
                    var planHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(revision.PlanJson)))
                        .Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(planHash, saveAttempt.PlanSha256, StringComparison.Ordinal)) return false;
                    var latest = snapshot.Verifications?.Where(item => item.JobId == snapshot.Job.Id &&
                        item.RevisionNumber == revision.RevisionNumber &&
                        item.CheckName == CadV2SavedVerificationRecorder.CheckName).LastOrDefault();
                    if (latest == null || !latest.Passed) return false;
                    var expected = JObject.Parse(latest.ExpectedJson);
                    var actual = JObject.Parse(latest.ActualJson);
                    if (CadParametricPlateContract.IsParametric(parsed.CandidateV2))
                    {
                        var parameterCheck = snapshot.Verifications?.Where(item => item.JobId == snapshot.Job.Id &&
                            item.RevisionNumber == revision.RevisionNumber && item.CheckName == "V2SavedParametricPlate").LastOrDefault();
                        if (parameterCheck == null || !parameterCheck.Passed) return false;
                        var parameterExpected = JObject.Parse(parameterCheck.ExpectedJson);
                        var parameterActual = JObject.Parse(parameterCheck.ActualJson);
                        if (!JToken.DeepEquals(parameterExpected, expected) || parameterActual.Value<string>("sha256") != model.LastSavedSha256) return false;
                        var observation = parameterActual["observed"]?["parametricPlate"]?.ToObject<CadParametricPlateObservation>();
                        if (observation == null || observation.ModelId != model.ModelId || observation.ConfigurationKey != model.ConfigurationKey ||
                            observation.SketchEntityId != parsed.CandidateV2.Steps[1].OutputEntityId ||
                            CadParametricPlateVerifier.Check(CadParametricPlateContract.ReadIntent(parsed.CandidateV2), observation).Count != 0) return false;
                    }
                    return expected.Value<string>("planSha256") == planHash &&
                        expected.Value<string>("modelId") == model.ModelId.ToString("D") &&
                        expected.Value<string>("modelRevisionId") == model.CurrentModelRevisionId.ToString("D") &&
                        string.Equals(expected.Value<string>("path"), canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                        expected.Value<string>("sha256") == model.LastSavedSha256 &&
                        actual.Value<string>("sha256") == model.LastSavedSha256;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                ex is WorkspacePolicyException || ex is Newtonsoft.Json.JsonException)
            { return false; }
        }
    }
}
