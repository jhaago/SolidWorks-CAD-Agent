using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository
    {
        private const string EditColumns="Id,JobId,RevisionId,ModelId,CheckpointModelRevisionId,ModelRevisionId,PlanSha256,WorkingPath,CheckpointPath,CheckpointSha256,OutputPath,SavedSha256,FailureReason,Status";
        public Task<CadParametricEditAttemptRecord> PrepareParametricEditAttemptAsync(Guid jobId,Guid revisionId,Guid childId,WorkspacePolicy workspace,CancellationToken token)
        {
            ThrowIfDisposed();token.ThrowIfCancellationRequested();GuidText(childId);
            try {
                using(var c=OpenConnection())using(var tx=c.BeginTransaction(IsolationLevel.Serializable)) {
                    var json=ReadCurrentEditPlan(c,tx,jobId,revisionId,true);
                    var plan=CadPlanDocumentReader.ReadPersisted(json).CandidateV2;
                    var source=ReadEditModel(c,tx,plan.ParametricEdit.ModelId);
                    if(source==null||source.Status!=CadModelIdentityStatus.ActiveSaved||source.CurrentModelRevisionId!=plan.ParametricEdit.ModelRevisionId||
                        source.LastSavedSha256!=plan.ParametricEdit.Sha256||source.ConfigurationKey!=plan.ParametricEdit.ConfigurationKey||source.DocumentKind!="Part")
                        throw new InvalidOperationException("The source identity changed before edit reservation.");
                    var now=DateTime.UtcNow;
                    var checkpointRevision=Guid.NewGuid();
                    var child=new CadModelIdentityRecord{ModelId=childId,ParentModelId=source.ModelId,DocumentKind="Part",Status=CadModelIdentityStatus.Pending,
                        CustomPropertyKey="SolidWorksCadAgent.ModelId",ConfigurationKey=source.ConfigurationKey,CurrentModelRevisionId=checkpointRevision,
                        SolidWorksRevision=source.SolidWorksRevision,RegistryVersion=1,CreatedUtc=now,UpdatedUtc=now};
                    var attempt=new CadParametricEditAttemptRecord{Id=Guid.NewGuid(),JobId=jobId,RevisionId=revisionId,ModelId=childId,
                        CheckpointModelRevisionId=checkpointRevision,ModelRevisionId=Guid.NewGuid(),PlanSha256=HashPlan(json),Status=CadParametricEditAttemptStatus.Prepared,
                        WorkingPath=workspace.ResolveForWrite(".agent-edits/"+childId.ToString("N")+"-work.sldprt",false),
                        CheckpointPath=workspace.ResolveForWrite(".agent-edits/"+childId.ToString("N")+"-checkpoint.sldprt",false),
                        OutputPath=workspace.ResolveForWrite(plan.Steps.Last().Parameters.Value<string>("path"),false)};
                    using(var cmd=c.CreateCommand()) {
                        cmd.Transaction=tx;cmd.CommandText=@"INSERT INTO ManagedModels (ModelId,ParentModelId,DocumentKind,Status,CustomPropertyKey,CanonicalPath,LastSavedSha256,CurrentModelRevisionId,ConfigurationKey,SolidWorksRevision,RegistryVersion,CreatedUtc,UpdatedUtc)
VALUES(@ModelId,@ParentModelId,@DocumentKind,@Status,@CustomPropertyKey,@CanonicalPath,@LastSavedSha256,@CurrentModelRevisionId,@ConfigurationKey,@SolidWorksRevision,@RegistryVersion,@CreatedUtc,@UpdatedUtc);";
                        BindModel(cmd,child);cmd.ExecuteNonQuery();
                    }
                    using(var cmd=c.CreateCommand()) {
                        cmd.Transaction=tx;cmd.CommandText="INSERT INTO V2ModelOwnerships(ModelId,JobId,FirstRevisionId,CreatedUtc) VALUES(@ModelId,@JobId,@RevisionId,@Now);";
                        EditParameters(cmd,attempt);cmd.ExecuteNonQuery();
                    }
                    using(var cmd=c.CreateCommand()) {
                        cmd.Transaction=tx;cmd.CommandText="INSERT INTO ParametricEditAttempts("+EditColumns+",CreatedUtc,UpdatedUtc) VALUES(@Id,@JobId,@RevisionId,@ModelId,@CheckpointModelRevisionId,@ModelRevisionId,@PlanSha256,@WorkingPath,@CheckpointPath,NULL,@OutputPath,NULL,NULL,'Prepared',@Now,@Now);";
                        EditParameters(cmd,attempt);cmd.ExecuteNonQuery();
                    }
                    tx.Commit();return Task.FromResult(attempt);
                }
            }catch(SQLiteException ex)when(ex.ResultCode==SQLiteErrorCode.Constraint){throw new InvalidOperationException("This edit revision or child identity is already reserved; edits cannot replay.",ex);}
        }
        public Task<CadParametricEditAttemptRecord> GetParametricEditAttemptAsync(Guid jobId,Guid revisionId,CancellationToken token)
        {
            ThrowIfDisposed();token.ThrowIfCancellationRequested();
            using(var c=OpenConnection())using(var cmd=c.CreateCommand()) {
                cmd.CommandText="SELECT "+EditColumns+" FROM ParametricEditAttempts WHERE JobId=@JobId AND RevisionId=@RevisionId;";
                Add(cmd,"@JobId",GuidText(jobId));Add(cmd,"@RevisionId",GuidText(revisionId));
                using(var reader=cmd.ExecuteReader())return Task.FromResult(reader.Read()?ReadEditAttempt(reader):null);
            }
        }
        public Task CommitParametricEditCheckpointAsync(Guid id,CadParametricEditCheckpoint checkpoint,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Prepared,true,token,(c,tx,a,plan)=>{
                var child=ReadEditModel(c,tx,a.ModelId);
                var baseline=CadParametricEditContract.SourcePart(plan);
                var ids=ExpectedEditBindingIds(a.ModelId,baseline);
                if(checkpoint==null||checkpoint.ModelId!=a.ModelId||checkpoint.WorkingPath!=a.WorkingPath||checkpoint.CheckpointPath!=a.CheckpointPath||
                    !ValidEditHash(checkpoint.Sha256)||checkpoint.Bindings==null||checkpoint.Bindings.Count!=ids.Count||
                    checkpoint.Bindings.Any(b=>b==null||b.ModelId!=a.ModelId||b.ConfigurationKey!=child.ConfigurationKey||b.Status!=CadEntityReferenceStatus.Active||b.CreatedAtModelRevisionId!=a.CheckpointModelRevisionId)||
                    !new HashSet<Guid>(checkpoint.Bindings.Select(b=>b.EntityId)).SetEquals(ids)||
                    !CadParametricEditObservation.Matches(baseline,child,checkpoint.Observed))
                    throw new InvalidOperationException("The checkpoint requires complete matching native ownership and baseline evidence.");
                foreach(var binding in checkpoint.Bindings) {
                    ValidateBinding(binding,false);
                    ValidateEditCapture(binding,child,baseline);
                    using(var cmd=c.CreateCommand()) {
                        cmd.Transaction=tx;cmd.CommandText=@"INSERT INTO EntityReferenceBindings(ModelId,EntityId,EntityKind,ConfigurationKey,NativeObjectKind,ReferenceFormatVersion,NativeReferenceBytes,CreatedAtModelRevisionId,LastResolvedModelRevisionId,SemanticFingerprintJson,Status,CreatedUtc,UpdatedUtc)
VALUES(@ModelId,@EntityId,@EntityKind,@ConfigurationKey,@NativeObjectKind,@ReferenceFormatVersion,@NativeReferenceBytes,@CreatedAtModelRevisionId,@LastResolvedModelRevisionId,@SemanticFingerprintJson,@Status,@CreatedUtc,@UpdatedUtc);";
                        BindEntityBinding(cmd,binding,CloneBytes(binding.NativeReferenceBytes));cmd.ExecuteNonQuery();
                    }
                }
                a.CheckpointSha256=checkpoint.Sha256;
                UpdateEditModel(c,tx,a,CadModelIdentityStatus.ActiveSaved,a.CheckpointModelRevisionId,a.WorkingPath,a.CheckpointSha256);
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Checkpointed);
            });
        public Task BeginParametricEditMutationAsync(Guid id,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Checkpointed,true,token,(c,tx,a,plan)=>{
                var source=ReadEditModel(c,tx,plan.ParametricEdit.ModelId);
                if(source==null||source.Status!=CadModelIdentityStatus.ActiveSaved||source.CurrentModelRevisionId!=plan.ParametricEdit.ModelRevisionId||source.LastSavedSha256!=plan.ParametricEdit.Sha256)
                    throw new InvalidOperationException("Source changed before the journalled native mutation.");
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.MutationStarted);
            });
        public Task CommitParametricEditAppliedAsync(Guid id,JObject observed,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.MutationStarted,true,token,(c,tx,a,plan)=>{
                if(!CadParametricEditObservation.Matches(CadParametricEditContract.ExpectedPart(plan),ReadEditModel(c,tx,a.ModelId),observed))
                    throw new InvalidOperationException("The changed native part does not match the approved edit.");
                UpdateEditModel(c,tx,a,CadModelIdentityStatus.ActiveUnsaved,a.ModelRevisionId,a.WorkingPath,a.CheckpointSha256);
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Applied);
            });
        public Task BeginParametricEditSaveAsync(Guid id,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Applied,true,token,(c,tx,a,plan)=>WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Saving));
        public Task CommitParametricEditSavedAsync(Guid id,string path,string hash,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Saving,true,token,(c,tx,a,plan)=>{
                if(path!=a.OutputPath||!ValidEditHash(hash))throw new InvalidOperationException("Save evidence differs from approved output.");
                a.SavedSha256=hash;UpdateEditModel(c,tx,a,CadModelIdentityStatus.ActiveSaved,a.ModelRevisionId,a.OutputPath,hash);
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Saved);
            });
        public Task PublishParametricEditAsync(Guid id,JObject observed,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Saved,true,token,(c,tx,a,plan)=>{
                var model=ReadEditModel(c,tx,a.ModelId);
                if(model.Status!=CadModelIdentityStatus.ActiveSaved||model.CurrentModelRevisionId!=a.ModelRevisionId||model.CanonicalPath!=a.OutputPath||model.LastSavedSha256!=a.SavedSha256||
                    !CadParametricEditObservation.Matches(CadParametricEditContract.ExpectedPart(plan),model,observed))
                    throw new InvalidOperationException("Publication requires complete saved native observations at the exact model revision.");
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Published);
            });
        public Task BeginParametricEditRestoreAsync(Guid id,string reason,CancellationToken token)=>
            EditTransaction(id,null,false,token,(c,tx,a,plan)=>{
                if(a.Status==CadParametricEditAttemptStatus.Prepared||a.Status==CadParametricEditAttemptStatus.Published||a.Status==CadParametricEditAttemptStatus.Restored||a.Status==CadParametricEditAttemptStatus.Uncertain||a.Status==CadParametricEditAttemptStatus.Restoring||!ValidEditHash(a.CheckpointSha256))
                    throw new InvalidOperationException("No recoverable checkpoint is available in this attempt state.");
                a.FailureReason=reason;WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Restoring);
            });
        public Task CommitParametricEditRestoredAsync(Guid id,JObject observed,CancellationToken token)=>
            EditTransaction(id,CadParametricEditAttemptStatus.Restoring,false,token,(c,tx,a,plan)=>{
                if(!CadParametricEditObservation.Matches(CadParametricEditContract.SourcePart(plan),ReadEditModel(c,tx,a.ModelId),observed))
                    throw new InvalidOperationException("Restoration requires independently verified complete baseline observations.");
                UpdateEditModel(c,tx,a,CadModelIdentityStatus.ActiveSaved,a.CheckpointModelRevisionId,a.WorkingPath,a.CheckpointSha256);
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Restored);
            });
        public Task MarkParametricEditUncertainAsync(Guid id,string reason,CancellationToken token)=>
            EditTransaction(id,null,false,token,(c,tx,a,plan)=>{
                if(a.Status==CadParametricEditAttemptStatus.Published||a.Status==CadParametricEditAttemptStatus.Restored)throw new InvalidOperationException("A terminal verified edit cannot be marked uncertain.");
                a.FailureReason=reason;UpdateEditModel(c,tx,a,CadModelIdentityStatus.Uncertain,a.ModelRevisionId,null,null);
                using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE EntityReferenceBindings SET Status='Uncertain',UpdatedUtc=@Now WHERE ModelId=@ModelId;";EditParameters(cmd,a);cmd.ExecuteNonQuery();}
                WriteEditState(c,tx,a,CadParametricEditAttemptStatus.Uncertain);
            });
        private Task EditTransaction(Guid id,CadParametricEditAttemptStatus? expected,bool executing,CancellationToken token,Action<SQLiteConnection,SQLiteTransaction,CadParametricEditAttemptRecord,CadPlanV2Document> action)
        {
            ThrowIfDisposed();token.ThrowIfCancellationRequested();
            using(var c=OpenConnection())using(var tx=c.BeginTransaction(IsolationLevel.Serializable)) {
                CadParametricEditAttemptRecord attempt;
                using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="SELECT "+EditColumns+" FROM ParametricEditAttempts WHERE Id=@Id;";Add(cmd,"@Id",GuidText(id));using(var reader=cmd.ExecuteReader())attempt=reader.Read()?ReadEditAttempt(reader):null;}
                if(attempt==null||expected.HasValue&&attempt.Status!=expected.Value)throw new InvalidOperationException("The edit attempt is missing or has already advanced; no replay permitted.");
                var json=ReadCurrentEditPlan(c,tx,attempt.JobId,attempt.RevisionId,executing);
                if(HashPlan(json)!=attempt.PlanSha256)throw new InvalidOperationException("The approved edit plan changed during execution.");
                action(c,tx,attempt,CadPlanDocumentReader.ReadPersisted(json).CandidateV2);tx.Commit();
            }return Task.CompletedTask;
        }
        private static string ReadCurrentEditPlan(SQLiteConnection c,SQLiteTransaction tx,Guid job,Guid revision,bool executing)
        {
            string json;
            using(var cmd=c.CreateCommand()) {
                cmd.Transaction=tx;cmd.CommandText=@"SELECT r.PlanJson FROM Revisions r JOIN Jobs j ON j.Id=r.JobId WHERE r.JobId=@JobId AND r.Id=@RevisionId AND j.IsSimulated=0 AND j.PlanValidated=1
AND r.Id=(SELECT Id FROM Revisions WHERE JobId=@JobId ORDER BY RevisionNumber DESC LIMIT 1)"+(executing?" AND j.State=@Executing":"")+";";
                Add(cmd,"@JobId",GuidText(job));Add(cmd,"@RevisionId",GuidText(revision));Add(cmd,"@Executing",(int)JobState.Executing);json=cmd.ExecuteScalar() as string;
            }
            var parsed=CadPlanDocumentReader.ReadPersisted(json);
            if(!parsed.IsValid||!CadParametricEditContract.IsEdit(parsed.CandidateV2))throw new InvalidOperationException("A current real validated source-bound edit revision is required.");
            return json;
        }
        private static CadModelIdentityRecord ReadEditModel(SQLiteConnection c,SQLiteTransaction tx,Guid id)
        {
            using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="SELECT ModelId,ParentModelId,DocumentKind,Status,CustomPropertyKey,CanonicalPath,LastSavedSha256,CurrentModelRevisionId,ConfigurationKey,SolidWorksRevision,RegistryVersion,CreatedUtc,UpdatedUtc FROM ManagedModels WHERE ModelId=@ModelId;";Add(cmd,"@ModelId",GuidText(id));using(var reader=cmd.ExecuteReader())return reader.Read()?ReadModel(reader):null;}
        }
        private static void UpdateEditModel(SQLiteConnection c,SQLiteTransaction tx,CadParametricEditAttemptRecord a,CadModelIdentityStatus status,Guid revision,string path,string hash)
        {
            using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE ManagedModels SET Status=@ModelStatus,CurrentModelRevisionId=@CurrentRevision,CanonicalPath=@Path,LastSavedSha256=@Hash,UpdatedUtc=@Now WHERE ModelId=@ModelId;";EditParameters(cmd,a);Add(cmd,"@ModelStatus",status.ToString());Add(cmd,"@CurrentRevision",GuidText(revision));Add(cmd,"@Path",path);Add(cmd,"@Hash",hash);if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Owned child model missing.");}
        }
        private static void WriteEditState(SQLiteConnection c,SQLiteTransaction tx,CadParametricEditAttemptRecord a,CadParametricEditAttemptStatus next)
        {
            using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE ParametricEditAttempts SET Status=@Next,CheckpointSha256=@CheckpointSha256,SavedSha256=@SavedSha256,FailureReason=@FailureReason,UpdatedUtc=@Now WHERE Id=@Id AND Status=@Old;";EditParameters(cmd,a);Add(cmd,"@Next",next.ToString());Add(cmd,"@Old",a.Status.ToString());if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Edit attempt concurrently advanced.");}
        }
        private static void EditParameters(SQLiteCommand cmd,CadParametricEditAttemptRecord a)
        {
            Add(cmd,"@Id",GuidText(a.Id));Add(cmd,"@JobId",GuidText(a.JobId));Add(cmd,"@RevisionId",GuidText(a.RevisionId));Add(cmd,"@ModelId",GuidText(a.ModelId));
            Add(cmd,"@CheckpointModelRevisionId",GuidText(a.CheckpointModelRevisionId));Add(cmd,"@ModelRevisionId",GuidText(a.ModelRevisionId));Add(cmd,"@PlanSha256",a.PlanSha256);
            Add(cmd,"@WorkingPath",a.WorkingPath);Add(cmd,"@CheckpointPath",a.CheckpointPath);Add(cmd,"@CheckpointSha256",a.CheckpointSha256);Add(cmd,"@OutputPath",a.OutputPath);Add(cmd,"@SavedSha256",a.SavedSha256);Add(cmd,"@FailureReason",a.FailureReason);Add(cmd,"@Now",DateText(DateTime.UtcNow));
        }
        private static CadParametricEditAttemptRecord ReadEditAttempt(SQLiteDataReader r)=>new CadParametricEditAttemptRecord{
            Id=Guid.Parse(r.GetString(0)),JobId=Guid.Parse(r.GetString(1)),RevisionId=Guid.Parse(r.GetString(2)),ModelId=Guid.Parse(r.GetString(3)),CheckpointModelRevisionId=Guid.Parse(r.GetString(4)),ModelRevisionId=Guid.Parse(r.GetString(5)),
            PlanSha256=r.GetString(6),WorkingPath=r.GetString(7),CheckpointPath=r.GetString(8),CheckpointSha256=NullableString(r,9),OutputPath=r.GetString(10),SavedSha256=NullableString(r,11),FailureReason=NullableString(r,12),Status=ParseEnum<CadParametricEditAttemptStatus>(r.GetString(13),"parametric edit status")};
        private static HashSet<Guid> ExpectedEditBindingIds(Guid model,CadPlanV2Document baseline)
        {
            var ids=new HashSet<Guid>();
            foreach(var index in CadParametricHoleContract.IsHole(baseline)?new[]{1,5}:new[]{1}) {
                var sketch=baseline.Steps[index].OutputEntityId.Value;ids.Add(sketch);
                foreach(var role in index==1?CadParametricIdentity.Roles:CadParametricIdentity.HoleRoles.ToArray())ids.Add(CadParametricIdentity.For(model,sketch,role));
            }return ids;
        }
        private static bool ValidEditHash(string value)=>value?.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        private static void ValidateEditCapture(CadEntityReferenceBinding binding,CadModelIdentityRecord child,CadPlanV2Document baseline)
        {
            foreach(var index in CadParametricHoleContract.IsHole(baseline)?new[]{1,5}:new[]{1}) {
                var sketch=baseline.Steps[index].OutputEntityId.Value;
                if(binding.EntityId==sketch) {
                    if(binding.EntityKind!="Sketch")throw new InvalidOperationException("Captured sketch kind differs.");
                    return;
                }
                foreach(var role in index==1?CadParametricIdentity.Roles:CadParametricIdentity.HoleRoles.ToArray()) {
                    if(binding.EntityId!=CadParametricIdentity.For(child.ModelId,sketch,role))continue;
                    JObject fingerprint;
                    try{fingerprint=JObject.Parse(binding.SemanticFingerprintJson??"{}");}
                    catch(Newtonsoft.Json.JsonException ex){throw new InvalidOperationException("Malformed captured edit fingerprint.",ex);}
                    var dimension=role=="width"||role=="length"||role=="thickness"||role=="holeDiameter"||role=="holeX"||role=="holeY";
                    var owner=role=="boss"||role=="thickness"?CadParametricIdentity.For(child.ModelId,sketch,"boss"):sketch;
                    var name=role=="width"?"AgentWidth":role=="length"?"AgentLength":role=="thickness"?"AgentThickness":role=="holeDiameter"?"AgentHoleDiameter":role=="holeX"?"AgentHoleX":"AgentHoleY";
                    if(binding.ReferenceFormatVersion!=3||binding.EntityKind!=(dimension?"DrivingDimension":role=="boss"?"Feature":"SketchSegment")||
                        binding.NativeObjectKind!=(dimension?"DimensionOwnerFeature":role=="boss"?"Feature":"SketchSegment")||
                        fingerprint.Value<int?>("evidenceVersion")!=1||fingerprint.Value<string>("modelId")!=child.ModelId.ToString("D")||
                        fingerprint.Value<string>("sketchEntityId")!=sketch.ToString("D")||fingerprint.Value<string>("configurationKey")!=child.ConfigurationKey||
                        fingerprint.Value<string>("role")!=role||fingerprint.Value<string>("ownerEntityId")!=owner.ToString("D")||
                        dimension&&fingerprint.Value<string>("nativeDimensionName")!=name)
                        throw new InvalidOperationException("Captured edit fingerprint, owner or native role differs.");
                    return;
                }
            }
            throw new InvalidOperationException("Unexpected edit capture.");
        }
        private static void RecoverParametricEdits(SQLiteConnection c,SQLiteTransaction tx,string now)
        {
            using(var cmd=c.CreateCommand()) {
                cmd.Transaction=tx;cmd.CommandText=@"UPDATE EntityReferenceBindings SET Status='Uncertain',UpdatedUtc=@Now WHERE ModelId IN (SELECT ModelId FROM ParametricEditAttempts WHERE Status NOT IN ('Published','Restored','Uncertain'));
UPDATE ManagedModels SET Status='Uncertain',UpdatedUtc=@Now WHERE ModelId IN (SELECT ModelId FROM ParametricEditAttempts WHERE Status NOT IN ('Published','Restored','Uncertain'));
UPDATE ParametricEditAttempts SET Status='Uncertain',FailureReason='Interrupted host execution; native mutation will not be replayed.',UpdatedUtc=@Now WHERE Status NOT IN ('Published','Restored','Uncertain');";
                Add(cmd,"@Now",now);cmd.ExecuteNonQuery();
            }
        }
    }
}
