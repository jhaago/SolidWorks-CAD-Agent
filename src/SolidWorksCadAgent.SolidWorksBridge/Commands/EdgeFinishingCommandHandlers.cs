// Adapted from soheil-mp/solidworks-mcp f450dab, Services/FeatureService.cs and PartFeatureService.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors. See third_party/solidworks-mcp/LICENSE.
// Changes: existing net48 STA, exact outer-loop selection, fresh-part scope, no propagation/retry, strict verification.
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class FilletEdgesCommandHandler : EdgeFinishingCommandHandlerBase
    {
        public FilletEdgesCommandHandler(ISolidWorksSession session):base(session) { }
        public override string Name=>CadCommandNames.FilletEdges;
        protected override EdgeFinishingKind Kind=>EdgeFinishingKind.Fillet;
        public override CadError Validate(JObject parameters)
        {var error=FaceFeatureValidation.ValidateFillet(parameters);return error==null?null:InvalidParameter(error);}
    }
    public sealed class ChamferEdgesCommandHandler : EdgeFinishingCommandHandlerBase
    {
        public ChamferEdgesCommandHandler(ISolidWorksSession session):base(session) { }
        public override string Name=>CadCommandNames.ChamferEdges;
        protected override EdgeFinishingKind Kind=>EdgeFinishingKind.Chamfer;
        public override CadError Validate(JObject parameters)
        {var error=FaceFeatureValidation.ValidateChamfer(parameters);return error==null?null:InvalidParameter(error);}
    }
    public abstract class EdgeFinishingCommandHandlerBase : SolidWorksCommandHandlerBase
    {
        protected EdgeFinishingCommandHandlerBase(ISolidWorksSession session):base(session) { }
        protected abstract EdgeFinishingKind Kind {get;}
        public override Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)=>InvokeAsync(application=>
        {
#if SOLIDWORKS_INTEROP
            var model=RequireDocument(application) as ModelDoc2;
            if(model==null)return Failure("NO_ACTIVE_DOCUMENT","Execute","A bound SOLIDWORKS part is required.");
            var context=DocumentContext(Session);bool invoked=false,touched=false;
            CadCommandResult result=null;Exception cleanupError=null;OperationCanceledException cancelled=null;
            try
            {
                context.FreshPartMutations.RequireUnsaved(model.GetPathName());
                if(model.SketchManager.ActiveSketch!=null)throw new InvalidOperationException("Exit the sketch before finishing edges.");
                var manager=(SelectionMgr)model.SelectionManager;
                if(manager.GetSelectedObjectCount2(-1)!=0)throw new InvalidOperationException("Clear existing selections before bounded finishing; the command will not consume them.");
                string axis=(string)parameters["normalAxis"],side=(string)parameters["side"];
                double size=parameters.Value<double>(Kind==EdgeFinishingKind.Fillet?"radiusMm":"distanceMm");
                var face=NativeFaceSelector.Resolve(model,axis,side,token);
                var edges=NativeFaceSelector.OuterEdges(model,face,token);
                var original=NativeEdgeFinishingInspector.CaptureOriginalEdges(edges);
                var before=CaptureInventory(model);
                string featureName=EdgeFinishingIntent.NextFeatureName(Kind,before),nativeType=Kind==EdgeFinishingKind.Fillet?"Fillet":"Chamfer";
                var selectData=manager.CreateSelectData();selectData.Mark=0;touched=true;
                foreach(var edge in edges)
                {token.ThrowIfCancellationRequested();if(!((IEntity)edge).Select4(true,selectData))throw new InvalidOperationException("A requested outer edge could not be selected.");}
                RequireSelection(manager,edges);token.ThrowIfCancellationRequested();
                EdgeFinishingDefinition definition=null;
                result=NativeFeatureMaterialVerifier.Execute(model,MaterialChangeKind.Remove,()=>
                {
                    token.ThrowIfCancellationRequested();RequireDocument(application);context.FreshPartMutations.RequireUnsaved(model.GetPathName());
                    if(model.SketchManager.ActiveSketch!=null)throw new InvalidOperationException("Sketch state changed before finishing.");
                    RequireSelection(manager,edges);
                    invoked=true; // No fallback/retry after an accepted or ambiguous native call.
                    return Create(model,size/1000);
                },feature=>
                {
                    feature.Name=featureName;
                    if(feature.Name!=featureName)throw new InvalidOperationException("The finishing feature name could not be verified.");
                    EdgeFinishingIntent.VerifyInventory(before,CaptureInventory(model),featureName,nativeType);
                    return new{normalAxis=axis,side,featureName,nativeType,edgeCount=edges.Count,definition,
                        definitionVerified=true,edgeGeometryVerified=true,featureInventoryVerified=true,
                        executionScope="newly created unsaved bound agent part",rollbackAttempted=false};
                },new[]{nativeType},feature=>
                {
                    RequireDocument(application);definition=NativeEdgeFinishingInspector.ReadDefinition(feature);
                    EdgeFinishingIntent.VerifyDefinition(Kind,size,edges.Count,definition);
                    if(Kind==EdgeFinishingKind.Fillet)EdgeFinishingIntent.VerifyRoundRadii(size,NativeEdgeFinishingInspector.RoundRadiiMm(feature));
                    else EdgeFinishingIntent.VerifyChamferLegs(size,original,NativeEdgeFinishingInspector.ChamferFaces(feature));
                },evidence=>
                {
                    if(evidence.Before.BodyCount!=1||evidence.After.BodyCount!=1)throw new InvalidOperationException("Finishing must preserve exactly one solid body.");
                });
                if(!invoked&&token.IsCancellationRequested)cancelled=new OperationCanceledException(token);
            }
            catch(OperationCanceledException ex) when(!invoked){cancelled=ex;}
            catch(Exception ex)
            {
                result=Failure(invoked?"FINISHING_EXECUTION_FAILED":"FINISHING_PREFLIGHT_FAILED",invoked?"Execute":"Validate",
                    invoked?"Native finishing failed. The part may contain changes; no rollback was attempted.":ex.Message,invoked?ex.Message:null);
            }
            finally
            {
                if(touched)try
                {model.ClearSelection2(true);if(((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1)!=0)throw new InvalidOperationException("Native finishing selections remain after cleanup.");}
                catch(Exception ex){cleanupError=ex;}
            }
            if(cancelled!=null&&cleanupError==null)throw cancelled;
            return FinishingCompletion.Finish(result,invoked,cleanupError,token.IsCancellationRequested,context.FreshPartMutations.Quarantine);
#else
            return InteropUnavailable();
#endif
        },token);
#if SOLIDWORKS_INTEROP
        private Feature Create(ModelDoc2 model,double metres)
        {
            if(Kind==EdgeFinishingKind.Fillet)
            {
                int options=(int)(swFeatureFilletOptions_e.swFeatureFilletUniformRadius|swFeatureFilletOptions_e.swFeatureFilletAttachEdges|swFeatureFilletOptions_e.swFeatureFilletKeepFeatures);
                return model.FeatureManager.FeatureFillet3(options,metres,0,0,(int)swFeatureFilletType_e.swFeatureFilletType_Simple,0,0,
                    null,null,null,null,null,null,null) as Feature;
            }
            return model.FeatureManager.InsertFeatureChamfer(0,(int)swChamferType_e.swChamferAngleDistance,metres,Math.PI/4,0,0,0,0) as Feature;
        }
        private static IReadOnlyDictionary<string,string> CaptureInventory(ModelDoc2 model)
        {
            var tree=SolidWorksInspector.GetFeatureTree(model);
            if(tree.Any(f=>f.ErrorCode!=0&&!f.IsWarning))throw new InvalidOperationException("The part has native feature errors.");
            return FeatureInventory.Capture(tree.Select(f=>new KeyValuePair<string,string>(f.Name,f.TypeName)));
        }
        private static void RequireSelection(SelectionMgr manager,IReadOnlyList<Edge> edges)
        {
            if(manager.GetSelectedObjectCount2(-1)!=edges.Count||manager.GetSelectedObjectCount2(0)!=edges.Count)
                throw new InvalidOperationException("Native selected-edge count or mark differs from the exact outer loop.");
            for(int i=0;i<edges.Count;i++)if(!SolidWorksDocumentContext.SameIdentity(edges[i],manager.GetSelectedObject6(i+1,0)))
                throw new InvalidOperationException("Native selected-edge identity differs from the resolved outer loop.");
        }
#endif
    }
}
