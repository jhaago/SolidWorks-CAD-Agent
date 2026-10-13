using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public static class CadParametricEditArtifactAuthorization
    {
        public static bool IsAuthorized(JobSnapshot snapshot,JobRevision revision,CommandExecutionRecord save,CadParametricEditAttemptRecord attempt,
            CadModelIdentityRecord model,Guid? owner,string path,WorkspacePolicy workspace)
        {
            if(snapshot?.Job==null||revision==null||save==null||attempt==null||model==null||workspace==null||snapshot.Job.IsSimulated||!snapshot.Job.PlanValidated||
                snapshot.Job.State!=JobState.ReadyForReview&&snapshot.Job.State!=JobState.Completed||snapshot.Revisions.LastOrDefault()?.Id!=revision.Id||
                owner!=snapshot.Job.Id||attempt.JobId!=snapshot.Job.Id||attempt.RevisionId!=revision.Id||attempt.Status!=CadParametricEditAttemptStatus.Published||
                attempt.ModelId!=model.ModelId||model.Status!=CadModelIdentityStatus.ActiveSaved||model.DocumentKind!="Part"||attempt.ModelRevisionId!=model.CurrentModelRevisionId||
                attempt.SavedSha256!=model.LastSavedSha256||attempt.SavedSha256?.Length!=64||attempt.CheckpointSha256?.Length!=64||
                save.JobId!=snapshot.Job.Id||save.RevisionNumber!=revision.RevisionNumber||save.CommandName!="SavePart"||!save.Success)return false;
            try {
                var parsed=CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
                if(!parsed.IsValid||!CadParametricEditContract.IsEdit(parsed.CandidateV2)||attempt.PlanSha256!=CadParametricEditContract.HashText(revision.PlanJson)||model.ParentModelId!=parsed.CandidateV2.ParametricEdit.ModelId)return false;
                var part=CadParametricEditContract.ExpectedPart(parsed.CandidateV2);
                var planned=workspace.ResolveForRead(part.Steps.Last().Parameters.Value<string>("path"));
                var result=JObject.Parse(save.ResultJson);
                if(planned!=path||model.CanonicalPath!=path||attempt.OutputPath!=path||workspace.ResolveForRead(result.Value<string>("path"))!=path||
                    result["errors"]?.Type!=JTokenType.Integer||result.Value<int>("errors")!=0||
                    !JToken.DeepEquals(JObject.Parse(save.ParametersJson),parsed.CandidateV2.Steps.Last().Parameters))return false;
                var check=snapshot.Verifications.LastOrDefault(v=>v.JobId==snapshot.Job.Id&&v.RevisionNumber==revision.RevisionNumber&&v.CheckName==CadV2SavedVerificationRecorder.CheckName);
                if(check?.Passed!=true)return false;
                var expected=JObject.Parse(check.ExpectedJson);var actual=JObject.Parse(check.ActualJson);
                if(expected.Value<string>("planSha256")!=attempt.PlanSha256||expected.Value<string>("modelId")!=model.ModelId.ToString("D")||
                    expected.Value<string>("modelRevisionId")!=model.CurrentModelRevisionId.ToString("D")||expected.Value<string>("path")!=path||
                    expected.Value<string>("sha256")!=model.LastSavedSha256||actual.Value<string>("sha256")!=model.LastSavedSha256||
                    expected.Value<string>("sourceModelId")!=parsed.CandidateV2.ParametricEdit.ModelId.ToString("D")||expected.Value<string>("sourceSha256")!=parsed.CandidateV2.ParametricEdit.Sha256||
                    !CadParametricEditObservation.Matches(part,model,actual["observed"] as JObject))return false;
                foreach(var name in new[]{"V2SavedParametricPlate"}.Concat(CadParametricHoleContract.IsHole(part)?new[]{"V2SavedParametricHole"}:Array.Empty<string>())) {
                    var parameters=snapshot.Verifications.LastOrDefault(v=>v.JobId==snapshot.Job.Id&&v.RevisionNumber==revision.RevisionNumber&&v.CheckName==name);
                    if(parameters?.Passed!=true||!JToken.DeepEquals(JObject.Parse(parameters.ExpectedJson),expected)||!JToken.DeepEquals(JObject.Parse(parameters.ActualJson),actual))return false;
                }
                return true;
            }catch(Exception ex)when(ex is ArgumentException||ex is JsonException||ex is IOException||ex is WorkspacePolicyException||ex is InvalidOperationException){return false;}
        }
    }
}
