using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public static class CadParametricEditSourceAuthorization
    {
        public static async Task<CadParametricEditSource> ResolveAsync(SqliteJobRepository repository,WorkspacePolicy workspace,Guid jobId,CancellationToken token)
        {
            var snapshot=await repository.GetSnapshotAsync(jobId,token).ConfigureAwait(false);
            var revision=snapshot?.Revisions.LastOrDefault();
            var parsed=CadPlanDocumentReader.ReadPersisted(revision?.PlanJson);
            if(snapshot?.Job==null||revision==null||!parsed.IsValid||parsed.Version!=2||CadParametricEditContract.IsEdit(parsed.CandidateV2)||!CadParametricPlateContract.IsParametric(parsed.CandidateV2))
                throw new InvalidOperationException("Choose a locally verified agent-created parametric plate or plate-with-hole job. Chained edits are outside this first slice.");
            var saved=snapshot.Verifications.LastOrDefault(v=>v.RevisionNumber==revision.RevisionNumber&&v.CheckName==CadV2SavedVerificationRecorder.CheckName);
            if(saved==null||!saved.Passed||!Guid.TryParse(JObject.Parse(saved.ExpectedJson).Value<string>("modelId"),out var modelId))
                throw new InvalidOperationException("The source job has no current successful saved-model evidence.");
            var model=await repository.GetModelAsync(modelId,token).ConfigureAwait(false);
            var owner=await repository.GetV2ModelOwnerAsync(modelId,token).ConfigureAwait(false);
            var save=snapshot.Commands.LastOrDefault(c=>c.RevisionNumber==revision.RevisionNumber&&c.CommandName=="SavePart"&&c.Success);
            var attempt=await repository.FindV2MutationAttemptAsync(jobId,revision.Id,parsed.CandidateV2.Steps.Last().StepKey,token).ConfigureAwait(false);
            var relative=parsed.CandidateV2.Steps.Last().Parameters.Value<string>("path");
            var path=workspace.ResolveForRead(relative);
            if(!CadV2ArtifactAuthorization.IsAuthorized(snapshot,revision,save,attempt,model,owner,path,workspace)||
                CheckpointedParametricEditorHash.HashFile(path)!=model.LastSavedSha256)
                throw new InvalidOperationException("The source is not at its exact current verified saved identity, revision and file hash.");
            return new CadParametricEditSource{JobId=jobId,RevisionId=revision.Id,ModelId=modelId,ModelRevisionId=model.CurrentModelRevisionId,
                ConfigurationKey=model.ConfigurationKey,Path=relative,Sha256=model.LastSavedSha256,PlanSha256=CadParametricEditContract.HashText(revision.PlanJson),SourcePlanJson=revision.PlanJson};
        }
        public static async Task RequireAsync(SqliteJobRepository repository,WorkspacePolicy workspace,CadParametricEditSource approved,CancellationToken token)
        {
            var current=await ResolveAsync(repository,workspace,approved.JobId,token).ConfigureAwait(false);
            if(!JToken.DeepEquals(JObject.FromObject(current),JObject.FromObject(approved)))throw new InvalidOperationException("The source changed after the edit was planned or approved.");
        }
    }
}
