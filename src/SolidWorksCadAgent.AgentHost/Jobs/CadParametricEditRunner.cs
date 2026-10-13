using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public sealed class CadParametricEditRunner
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;
        private readonly WorkspacePolicy _workspace;
        public CadParametricEditRunner(SqliteJobRepository repository,ICadCommandExecutor executor,WorkspacePolicy workspace)
        {_repository=repository;_executor=executor;_workspace=workspace;}

        public async Task<bool> RunAsync(Guid jobId,Guid revisionId,string approvedHash,CancellationToken token)
        {
            var snapshot=await _repository.GetSnapshotAsync(jobId,token).ConfigureAwait(false);
            var revision=snapshot?.Revisions.LastOrDefault();
            var parsed=CadPlanDocumentReader.ReadPersisted(revision?.PlanJson);
            if(revision?.Id!=revisionId||!parsed.IsValid||!CadParametricEditContract.IsEdit(parsed.CandidateV2)||
                !(_executor is ICheckpointedParametricEditExecutor editor)||!editor.SupportsCheckpointedParametricEditing||!(_executor is IVersionedCadCommandExecutor versioned))
                throw new InvalidOperationException("A current approved native checkpointed edit is required.");
            var plan=parsed.CandidateV2;
            var expected=CadParametricEditContract.ExpectedPart(plan);
            await RequireCurrentAsync(jobId,revisionId,approvedHash,plan,token).ConfigureAwait(false);
            var attempt=await _repository.PrepareParametricEditAttemptAsync(jobId,revisionId,Guid.NewGuid(),_workspace,token).ConfigureAwait(false);
            CadParametricEditCheckpoint checkpoint=null;
            try {
                var id=attempt.ModelId.ToString("N");
                checkpoint=await editor.ForkParametricPartAsync(new CadParametricEditNativeRequest{ExecutionId=jobId,ChildModelId=attempt.ModelId,Source=plan.ParametricEdit,
                    WorkingPath=".agent-edits/"+id+"-work.sldprt",CheckpointPath=".agent-edits/"+id+"-checkpoint.sldprt"},token).ConfigureAwait(false);
                if(CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(checkpoint.CheckpointPath))!=checkpoint.Sha256||
                    CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(checkpoint.WorkingPath))!=checkpoint.Sha256)
                    throw new InvalidOperationException("The saved owned checkpoint bytes changed before commit.");
                await _repository.CommitParametricEditCheckpointAsync(attempt.Id,checkpoint,token).ConfigureAwait(false);
                await RecordCommandAsync(jobId,revision,1,plan.Steps[0],new JObject{{"modelId",attempt.ModelId.ToString("D")},{"parentModelId",plan.ParametricEdit.ModelId.ToString("D")},{"checkpointPath",checkpoint.CheckpointPath},{"checkpointSha256",checkpoint.Sha256}},token).ConfigureAwait(false);
                await RequireCurrentAsync(jobId,revisionId,approvedHash,plan,token).ConfigureAwait(false);
                await _repository.BeginParametricEditMutationAsync(attempt.Id,token).ConfigureAwait(false);
                var changed=await editor.ApplyCheckpointedParameterAsync(jobId,attempt.ModelId,plan,token).ConfigureAwait(false);
                await _repository.CommitParametricEditAppliedAsync(attempt.Id,changed,token).ConfigureAwait(false);
                await RecordCommandAsync(jobId,revision,2,plan.Steps[1],changed,token).ConfigureAwait(false);
                await RequireCurrentAsync(jobId,revisionId,approvedHash,plan,token).ConfigureAwait(false);
                _workspace.ResolveForWrite(attempt.OutputPath,false);
                var beforeSave=await editor.InspectCheckpointedPartAsync(jobId,attempt.ModelId,expected,token).ConfigureAwait(false);
                var child=await _repository.GetModelAsync(attempt.ModelId,token).ConfigureAwait(false);
                if(!CadParametricEditObservation.Matches(expected,child,beforeSave))throw new InvalidOperationException("Independent pre-save readback failed.");
                await _repository.BeginParametricEditSaveAsync(attempt.Id,token).ConfigureAwait(false);
                var save=await versioned.ExecuteVersionedAsync(new CadVersionedCommandRequest(2,1,new CadCommandEnvelope{Command="SavePart",ExecutionId=jobId,ManagedModelId=attempt.ModelId,
                    Parameters=(JObject)plan.Steps[2].Parameters.DeepClone()}),token).ConfigureAwait(false);
                if(save==null||!save.Success||save.Error!=null||save.Data?.Value<string>("path")!=attempt.OutputPath)
                    throw new InvalidOperationException("The owned edited part could not be saved: "+save?.Error?.Message);
                var savedHash=CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(attempt.OutputPath));
                await _repository.CommitParametricEditSavedAsync(attempt.Id,attempt.OutputPath,savedHash,token).ConfigureAwait(false);
                await RecordCommandAsync(jobId,revision,3,plan.Steps[2],save.Data,token).ConfigureAwait(false);
                var close=await _executor.ExecuteAsync(new CadCommandEnvelope{Command="CloseDocument",ExecutionId=jobId,ManagedModelId=attempt.ModelId,Parameters=new JObject()},token).ConfigureAwait(false);
                if(close?.Success!=true||close.Data?.Value<bool?>("closed")!=true)throw new InvalidOperationException("The saved edit could not be closed for readback.");
                var open=await _executor.ExecuteAsync(new CadCommandEnvelope{Command="OpenPart",ExecutionId=jobId,Parameters=new JObject{{"path",attempt.OutputPath}}},token).ConfigureAwait(false);
                if(open?.Success!=true||open.Error!=null||open.Data?.Value<string>("path")!=attempt.OutputPath)throw new InvalidOperationException("The exact managed saved edit could not be reopened.");
                var observed=await editor.InspectCheckpointedPartAsync(jobId,attempt.ModelId,expected,token).ConfigureAwait(false);
                child=await _repository.GetModelAsync(attempt.ModelId,token).ConfigureAwait(false);
                if(!CadParametricEditObservation.Matches(expected,child,observed)||CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(attempt.OutputPath))!=savedHash)
                    throw new InvalidOperationException("Saved parameter, physical geometry or hash verification failed.");
                await RequireCurrentAsync(jobId,revisionId,approvedHash,plan,token).ConfigureAwait(false);
                attempt=await _repository.GetParametricEditAttemptAsync(jobId,revisionId,token).ConfigureAwait(false);
                await RecordSavedEvidenceAsync(jobId,revision,attempt,child,observed,true,null,token).ConfigureAwait(false);
                await _repository.PublishParametricEditAsync(attempt.Id,observed,token).ConfigureAwait(false);
                return true;
            }catch(Exception failure) {
                string recovery;
                try {
                    if(checkpoint==null)throw new InvalidOperationException("No completed native checkpoint is available.");
                    await _repository.BeginParametricEditRestoreAsync(attempt.Id,failure.Message,CancellationToken.None).ConfigureAwait(false);
                    var restored=await editor.RestoreParametricCheckpointAsync(jobId,attempt.ModelId,checkpoint.Sha256,CancellationToken.None).ConfigureAwait(false);
                    if(CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(checkpoint.WorkingPath))!=checkpoint.Sha256)
                        throw new InvalidOperationException("Restored bytes do not match the durable checkpoint.");
                    await _repository.CommitParametricEditRestoredAsync(attempt.Id,restored,CancellationToken.None).ConfigureAwait(false);
                    recovery="Checkpoint restored and independently verified.";
                    await _repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=jobId,RevisionNumber=revision.RevisionNumber,CheckName="ParametricEditCheckpointRestore",Passed=true,
                        ExpectedJson=new JObject{{"modelId",attempt.ModelId.ToString("D")},{"checkpointSha256",checkpoint.Sha256},{"sourceSha256",plan.ParametricEdit.Sha256}}.ToString(Formatting.None),
                        ActualJson=new JObject{{"observed",restored},{"sha256",checkpoint.Sha256}}.ToString(Formatting.None),CreatedUtc=DateTime.UtcNow},CancellationToken.None).ConfigureAwait(false);
                }catch(Exception restoreFailure) {
                    recovery="Recovery is uncertain; the checkpoint is retained. "+restoreFailure.Message;
                    try {await _repository.MarkParametricEditUncertainAsync(attempt.Id,failure.Message+" "+recovery,CancellationToken.None).ConfigureAwait(false);}
                    catch(Exception journalFailure){failure.Data["EditRecoveryJournalFailure"]=journalFailure;}
                }
                var currentAttempt=await _repository.GetParametricEditAttemptAsync(jobId,revisionId,CancellationToken.None).ConfigureAwait(false);
                var child=await _repository.GetModelAsync(attempt.ModelId,CancellationToken.None).ConfigureAwait(false);
                await RecordSavedEvidenceAsync(jobId,revision,currentAttempt,child,null,false,failure.Message+" "+recovery,CancellationToken.None).ConfigureAwait(false);
                await _repository.AppendCommandAsync(new CommandExecutionRecord{Id=Guid.NewGuid(),JobId=jobId,RevisionNumber=revision.RevisionNumber,SequenceNumber=4,CommandName="ParametricEditFailure",Success=false,
                    ParametersJson="{}",ResultJson="{}",ErrorCode="PARAMETRIC_EDIT_FAILED",ErrorMessage=failure.Message+" "+recovery,StartedUtc=DateTime.UtcNow,CompletedUtc=DateTime.UtcNow},CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
        private async Task RequireCurrentAsync(Guid job,Guid revision,string hash,CadPlanV2Document plan,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var snapshot=await _repository.GetSnapshotAsync(job,token).ConfigureAwait(false);
            if(snapshot?.Job==null||snapshot.Job.State!=JobState.Executing||snapshot.Job.IsSimulated||!snapshot.Job.PlanValidated||snapshot.Revisions.LastOrDefault()?.Id!=revision||
                CadParametricEditContract.HashText(snapshot.Revisions.Last().PlanJson)!=hash)throw new InvalidOperationException("The exact approved executing edit revision is required.");
            await CadParametricEditSourceAuthorization.RequireAsync(_repository,_workspace,plan.ParametricEdit,token).ConfigureAwait(false);
        }
        private Task RecordCommandAsync(Guid job,JobRevision revision,int sequence,CadPlanV2Step step,JObject result,CancellationToken token)=>
            _repository.AppendCommandAsync(new CommandExecutionRecord{Id=Guid.NewGuid(),JobId=job,RevisionNumber=revision.RevisionNumber,SequenceNumber=sequence,CommandName=step.Command,
                ParametersJson=step.Parameters.ToString(Formatting.None),ResultJson=result.ToString(Formatting.None),Success=true,StartedUtc=DateTime.UtcNow,CompletedUtc=DateTime.UtcNow},token);
        private async Task RecordSavedEvidenceAsync(Guid job,JobRevision revision,CadParametricEditAttemptRecord attempt,CadModelIdentityRecord model,JObject observed,bool passed,string reason,CancellationToken token)
        {
            var plan=CadPlanDocumentReader.ReadPersisted(revision.PlanJson).CandidateV2;
            var expected=new JObject{{"planSha256",attempt.PlanSha256},{"modelId",attempt.ModelId.ToString("D")},{"modelRevisionId",attempt.ModelRevisionId.ToString("D")},
                {"path",attempt.OutputPath},{"sha256",attempt.SavedSha256},{"sourceModelId",plan.ParametricEdit.ModelId.ToString("D")},{"sourceSha256",plan.ParametricEdit.Sha256}};
            var actual=new JObject{{"sha256",passed?attempt.SavedSha256:null},{"observed",observed??(JToken)JValue.CreateNull()},{"reason",reason}};
            var names=new[]{CadV2SavedVerificationRecorder.CheckName,"V2SavedParametricPlate"}.Concat(CadParametricHoleContract.IsHole(CadParametricEditContract.ExpectedPart(plan))?new[]{"V2SavedParametricHole"}:Array.Empty<string>());
            foreach(var name in names)await _repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=job,RevisionNumber=revision.RevisionNumber,CheckName=name,Passed=passed,
                ExpectedJson=expected.ToString(Formatting.None),ActualJson=actual.ToString(Formatting.None),CreatedUtc=DateTime.UtcNow},token).ConfigureAwait(false);
        }
    }
}
