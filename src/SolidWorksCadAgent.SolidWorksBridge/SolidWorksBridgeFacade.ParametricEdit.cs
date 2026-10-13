using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;

namespace SolidWorksCadAgent.SolidWorksBridge
{
    public sealed partial class SolidWorksBridgeFacade
    {
        private readonly WorkspacePolicy _editWorkspace;
        private readonly CheckpointedParametricEditor _parametricEditor=new CheckpointedParametricEditor();
        public bool SupportsCheckpointedParametricEditing=>SupportsManagedReferences;

        public async Task<CadParametricEditCheckpoint> ForkParametricPartAsync(CadParametricEditNativeRequest request,CancellationToken token)
        {
            await _commandGate.WaitAsync(token).ConfigureAwait(false);
            try {
                if(_disposed||!SupportsCheckpointedParametricEditing||request?.Source==null||request.AuthorizeSideEffect==null||request.ExecutionId==Guid.Empty||request.ChildModelId==Guid.Empty)
                    throw new InvalidOperationException("A native managed edit context is required.");
                var source=await _modelReferenceStore.GetModelAsync(request.Source.ModelId,token).ConfigureAwait(false);
                var child=await _modelReferenceStore.GetModelAsync(request.ChildModelId,token).ConfigureAwait(false);
                var path=_editWorkspace.ResolveForRead(request.Source.Path);
                if(source==null||source.Status!=CadModelIdentityStatus.ActiveSaved||source.DocumentKind!="Part"||source.CanonicalPath!=path||
                    source.CurrentModelRevisionId!=request.Source.ModelRevisionId||source.LastSavedSha256!=request.Source.Sha256||source.ConfigurationKey!=request.Source.ConfigurationKey||
                    child==null||child.Status!=CadModelIdentityStatus.Pending||child.ParentModelId!=source.ModelId||child.ConfigurationKey!=source.ConfigurationKey||
                    request.Source.PlanSha256!=CadParametricEditContract.HashText(request.Source.SourcePlanJson))
                    throw new InvalidOperationException("The source or reserved child identity differs from the approved edit.");
                var parsed=CadPlanDocumentReader.ReadPersisted(request.Source.SourcePlanJson);
                if(!parsed.IsValid||CadParametricEditContract.IsEdit(parsed.CandidateV2)||!CadParametricPlateContract.IsParametric(parsed.CandidateV2))
                    throw new InvalidOperationException("A verified parametric creation source is required.");
                var plan=parsed.CandidateV2;
                var bindings=new List<CadEntityReferenceBinding>();
                foreach(var sketch in new[]{plan.Steps[1].OutputEntityId.Value}.Concat(CadParametricHoleContract.IsHole(plan)?new[]{plan.Steps[5].OutputEntityId.Value}:Array.Empty<Guid>())) {
                    bindings.Add(await _modelReferenceStore.GetEntityBindingAsync(source.ModelId,sketch,source.ConfigurationKey,token).ConfigureAwait(false));
                    foreach(var role in sketch==plan.Steps[1].OutputEntityId.Value?CadParametricIdentity.Roles:CadParametricIdentity.HoleRoles.ToArray())
                        bindings.Add(await _modelReferenceStore.GetEntityBindingAsync(source.ModelId,CadParametricIdentity.For(source.ModelId,sketch,role),source.ConfigurationKey,token).ConfigureAwait(false));
                }
                if(bindings.Any(b=>b==null))throw new InvalidOperationException("The verified source has missing captured references.");
                return await _session.InvokeWithApplicationAsync(app=>_parametricEditor.Fork(app,
                    SolidWorksCommandHandlerBase.DocumentContext(_commandSession),_editWorkspace,request,source,child,plan,bindings),token).ConfigureAwait(false);
            }finally{_commandGate.Release();}
        }
        public Task<JObject> ApplyCheckpointedParameterAsync(Guid executionId,Guid childModelId,CadPlanV2Document approvedPlan,CancellationToken token)=>
            EditCallAsync((app,context)=>_parametricEditor.Apply(app,context,executionId,childModelId,approvedPlan),token);
        public Task<JObject> InspectCheckpointedPartAsync(Guid executionId,Guid childModelId,CadPlanV2Document expectedPart,CancellationToken token)=>
            EditCallAsync((app,context)=>_parametricEditor.Inspect(app,context,executionId,childModelId,expectedPart),token);
        public Task<JObject> RestoreParametricCheckpointAsync(Guid executionId,Guid childModelId,string checkpointSha256,CancellationToken token)=>
            EditCallAsync((app,context)=>_parametricEditor.Restore(app,context,executionId,childModelId,checkpointSha256),token);
        private async Task<JObject> EditCallAsync(Func<object,Session.SolidWorksDocumentContext,JObject> action,CancellationToken token)
        {
            await _commandGate.WaitAsync(token).ConfigureAwait(false);
            try {
                if(_disposed||!SupportsCheckpointedParametricEditing)throw new InvalidOperationException("Native managed parameter editing is unavailable.");
                return await _session.InvokeWithApplicationAsync(app=>action(app,SolidWorksCommandHandlerBase.DocumentContext(_commandSession)),token).ConfigureAwait(false);
            }finally{_commandGate.Release();}
        }
    }
}
