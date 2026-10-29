using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Parametric
{
    // All state and COM access is inside the facade's command gate and session STA.
    internal sealed class CheckpointedParametricEditor
    {
#if SOLIDWORKS_INTEROP
        private CadParametricEditNativeRequest _request;
        private CadModelIdentityRecord _child;
        private CadPlanV2Document _baseline;
        private IReadOnlyList<CadEntityReferenceBinding> _bindings;
        private WorkspacePolicy _workspace;
        private string _workingPath,_checkpointPath,_checkpointHash,_sourcePath;
        private bool _mutationInvoked,_restored;
        private IReadOnlyDictionary<string,string> _featureInventory;
#endif
        public CadParametricEditCheckpoint Fork(object appObject,SolidWorksDocumentContext context,WorkspacePolicy workspace,
            CadParametricEditNativeRequest request,CadModelIdentityRecord source,CadModelIdentityRecord child,CadPlanV2Document baseline,IReadOnlyList<CadEntityReferenceBinding> bindings)
        {
#if SOLIDWORKS_INTEROP
            var app=(SldWorks)appObject;
            var id=child.ModelId.ToString("N");
            if(request.WorkingPath!=".agent-edits/"+id+"-work.sldprt"||request.CheckpointPath!=".agent-edits/"+id+"-checkpoint.sldprt")
                throw new InvalidOperationException("The edit must use exact private child-owned paths.");
            var working=workspace.ResolveForWrite(request.WorkingPath,false);
            var checkpoint=workspace.ResolveForWrite(request.CheckpointPath,false);
            RejectOpenFilename(app,working);RejectOpenFilename(app,checkpoint);
            if(CheckpointedParametricEditorHash.HashFile(source.CanonicalPath)!=request.Source.Sha256)
                throw new InvalidOperationException("Source file changed before fork.");
            Directory.CreateDirectory(Path.GetDirectoryName(working));
            workspace.ResolveForWrite(working,false);workspace.ResolveForWrite(checkpoint,false);
            request.AuthorizeSideEffect();
            CopyNew(source.CanonicalPath,working);
            if(CheckpointedParametricEditorHash.HashFile(working)!=request.Source.Sha256||CheckpointedParametricEditorHash.HashFile(source.CanonicalPath)!=request.Source.Sha256)
                throw new InvalidOperationException("Source changed while its owned copy was created.");
            ModelDoc2 document=null;
            try {
                document=Open(app,working);
                RequireStamp(document,source.ModelId);
                Check(document,source,baseline,bindings);
                var rebased=Rebase(bindings,source.ModelId,child,baseline);
                document.Extension.CustomPropertyManager[""].Add3("SolidWorksCadAgent.ModelId",(int)swCustomInfoType_e.swCustomInfoText,
                    child.ModelId.ToString("D"),(int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                RequireStamp(document,child.ModelId);
                int errors=0,warnings=0;
                if(!document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,ref errors,ref warnings)||errors!=0)
                    throw new InvalidOperationException("The child identity could not be saved before checkpointing.");
                var observed=Check(document,child,baseline,rebased);
                var featureInventory=CaptureInventory(document);
                workspace.ResolveForWrite(checkpoint,false);CopyNew(working,checkpoint);
                var hash=CheckpointedParametricEditorHash.HashFile(checkpoint);
                if(hash!=CheckpointedParametricEditorHash.HashFile(working))throw new InvalidOperationException("Checkpoint copy differs from its saved child.");
                if(CheckpointedParametricEditorHash.HashFile(source.CanonicalPath)!=request.Source.Sha256)throw new InvalidOperationException("Source changed during fork.");
                context.BeginExecution(request.ExecutionId);context.SetExecutionIdentity(child.ModelId,null);context.Bind(document);
                _request=JObject.FromObject(request).ToObject<CadParametricEditNativeRequest>();
                _request.AuthorizeSideEffect=request.AuthorizeSideEffect;
                _child=child;_baseline=baseline;_bindings=rebased;_workspace=workspace;
                _sourcePath=source.CanonicalPath;_workingPath=working;_checkpointPath=checkpoint;_checkpointHash=hash;_mutationInvoked=false;_restored=false;
                _featureInventory=featureInventory;
                return new CadParametricEditCheckpoint{ModelId=child.ModelId,WorkingPath=working,CheckpointPath=checkpoint,Sha256=hash,Bindings=rebased,Observed=observed};
            }catch {
                if(document!=null&&SolidWorksDocumentContext.SameIdentity(document,app.IActiveDoc2))app.CloseDoc(document.GetTitle());
                throw;
            }
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
        public JObject Apply(object appObject,SolidWorksDocumentContext context,Guid execution,Guid child,CadPlanV2Document approvedPlan)
        {
#if SOLIDWORKS_INTEROP
            var document=Require(appObject,context,execution,child);
            if(_mutationInvoked||_restored)throw new InvalidOperationException("Checkpointed mutations cannot replay.");
            var errors=CadParametricEditContract.Validate(approvedPlan);
            if(errors.Count>0||!JToken.DeepEquals(JObject.FromObject(approvedPlan.ParametricEdit),JObject.FromObject(_request.Source)))
                throw new InvalidOperationException("The approved source-bound plan differs from the checkpoint.");
            Check(document,_child,_baseline,_bindings);
            RequireInventory(document);
            RequireSourceHash();
            var role=approvedPlan.Steps[1].Parameters.Value<string>("parameter");
            var value=approvedPlan.Steps[1].Parameters.Value<double>("valueMm");
            var sketch=_baseline.Steps[role.StartsWith("hole",StringComparison.Ordinal)?5:1].OutputEntityId.Value;
            var binding=_bindings.Single(b=>b.EntityId==CadParametricIdentity.For(child,sketch,role));
            var owner=ParametricPlateInspector.Resolve(document,binding) as Feature;
            if(owner==null||binding.NativeObjectKind!="DimensionOwnerFeature")throw new InvalidOperationException("Captured parameter owner missing.");
            var name=JObject.Parse(binding.SemanticFingerprintJson).Value<string>("nativeDimensionName");
            var dimensions=new List<Dimension>();
            for(var display=(DisplayDimension)owner.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)owner.GetNextDisplayDimension(display)) {
                var dimension=(Dimension)display.GetDimension2(0);
                if(dimension.FullName.Split('@')[0]==name)dimensions.Add(dimension);
            }
            if(dimensions.Count!=1||dimensions[0].DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving)
                throw new InvalidOperationException("Exactly one independently inspected captured driving dimension is required.");
            RequireSourceHash();
            _request.AuthorizeSideEffect();
            _mutationInvoked=true; // Also prevents retry if COM returns an ambiguous outcome.
            if(dimensions[0].SetSystemValue3(value/1000,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0||!document.ForceRebuild3(false))
                throw new InvalidOperationException("Native driving parameter change or regeneration failed.");
            var observed=Check(document,_child,CadParametricEditContract.ExpectedPart(approvedPlan),_bindings);
            observed["featureInventoryCheck"]=CadCommandResult.Ok(RequireInventory(document)).Data;
            RequireSourceHash();return observed;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
        public JObject Inspect(object app,SolidWorksDocumentContext context,Guid execution,Guid child,CadPlanV2Document expected)
        {
#if SOLIDWORKS_INTEROP
            var document=Require(app,context,execution,child);
            var observed=Check(document,_child,expected,_bindings);
            observed["featureInventoryCheck"]=CadCommandResult.Ok(RequireInventory(document)).Data;
            RequireSourceHash();return observed;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
        public JObject Restore(object appObject,SolidWorksDocumentContext context,Guid execution,Guid child,string hash)
        {
#if SOLIDWORKS_INTEROP
            if(_request==null||_request.ExecutionId!=execution||_request.ChildModelId!=child||
                _restored||hash!=_checkpointHash||CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(_checkpointPath))!=hash)
                throw new InvalidOperationException("Checkpoint identity/hash differs or restoration already occurred.");
            RequireSourceHash();
            var app=(SldWorks)appObject;
            if(context.IsClosedForExecution(execution)) {
                // A successful owned close clears the binding. Do not close whichever
                // unrelated document SOLIDWORKS made active after that close.
                RejectOpenFilename(app,_workingPath);
                RejectOpenFilename(app,_checkpointPath);
                foreach(var open in (app.GetDocuments() as object[]??Array.Empty<object>()).OfType<ModelDoc2>()) {
                    open.Extension.CustomPropertyManager[""].Get6("SolidWorksCadAgent.ModelId",false,out var value,out _,out _,out _);
                    if(Guid.TryParse(value,out var id)&&id==child)throw new InvalidOperationException("An unbound open document conflicts with the checkpointed child.");
                }
            }else {
                var document=Require(appObject,context,execution,child);
                app.CloseDoc(document.GetTitle());context.Clear();
            }
            _workspace.ResolveForWrite(_workingPath,true);
            File.Copy(_checkpointPath,_workingPath,true);
            if(CheckpointedParametricEditorHash.HashFile(_workingPath)!=hash)throw new InvalidOperationException("Restored child bytes differ from the checkpoint.");
            RejectOpenFilename(app,_workingPath);
            var restored=Open(app,_workingPath);RequireStamp(restored,child);
            context.BeginExecution(execution);context.SetExecutionIdentity(child,null);context.Bind(restored);
            var observed=Check(restored,_child,_baseline,_bindings);
            observed["featureInventoryCheck"]=CadCommandResult.Ok(RequireInventory(restored)).Data;
            RequireSourceHash();_restored=true;return observed;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
#if SOLIDWORKS_INTEROP
        private static IReadOnlyDictionary<string,string> CaptureInventory(ModelDoc2 document)=>
            FeatureInventory.Capture(SolidWorksInspector.GetFeatureTree(document).Select(f=>new KeyValuePair<string,string>(f.Name,f.TypeName)));
        private FeatureInventoryDifference RequireInventory(ModelDoc2 document)
        {
            var difference=FeatureInventory.Compare(_featureInventory,CaptureInventory(document));
            if(!difference.Matches)throw new InvalidOperationException("Checkpointed parameter edit changed the feature inventory: "+JObject.FromObject(difference));
            return difference;
        }
        private ModelDoc2 Require(object appObject,SolidWorksDocumentContext context,Guid execution,Guid child)
        {
            if(_request==null||_request.ExecutionId!=execution||_request.ChildModelId!=child||context.ManagedModelId!=child)
                throw new InvalidOperationException("Only the exact owned checkpointed execution can access this document.");
            var document=(ModelDoc2)context.RequireActive(((SldWorks)appObject).IActiveDoc2);
            RequireStamp(document,child);
            if(document.ConfigurationManager.ActiveConfiguration.Name!=_child.ConfigurationKey)throw new InvalidOperationException("Edit configuration changed.");
            return document;
        }
        private void RequireSourceHash()
        {
            if(CheckpointedParametricEditorHash.HashFile(_workspace.ResolveForRead(_sourcePath))!=_request.Source.Sha256)
                throw new InvalidOperationException("The immutable verified source changed during editing.");
        }
        private static void RequireStamp(ModelDoc2 document,Guid id)
        {
            document.Extension.CustomPropertyManager[""].Get6("SolidWorksCadAgent.ModelId",false,out var value,out _,out _,out _);
            if(!Guid.TryParse(value,out var actual)||actual!=id)throw new InvalidOperationException("Native model identity stamp differs.");
        }
        private static ModelDoc2 Open(SldWorks app,string path)
        {
            int errors=0,warnings=0;
            var document=(ModelDoc2)app.OpenDoc6(path,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref errors,ref warnings);
            if(document==null||errors!=0||!SolidWorksDocumentContext.SameIdentity(document,app.IActiveDoc2))throw new InvalidOperationException("The owned part could not be opened and activated.");
            return document;
        }
        private static void RejectOpenFilename(SldWorks app,string path)
        {
            var name=Path.GetFileName(path);
            if((app.GetDocuments() as object[]??Array.Empty<object>()).OfType<ModelDoc2>().Any(d=>
                string.Equals(Path.GetFileName(d.GetPathName()),name,StringComparison.OrdinalIgnoreCase)||string.Equals(d.GetTitle(),name,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An open document conflicts with the private edit filename.");
        }
        private static void CopyNew(string source,string target)
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read))input.CopyTo(output);
        }
        private static IReadOnlyList<CadEntityReferenceBinding> Rebase(IReadOnlyList<CadEntityReferenceBinding> source,Guid sourceId,CadModelIdentityRecord child,CadPlanV2Document plan)
        {
            var ids=new Dictionary<Guid,Guid>();
            var sketches=new[]{plan.Steps[1].OutputEntityId.Value}.Concat(CadParametricHoleContract.IsHole(plan)?new[]{plan.Steps[5].OutputEntityId.Value}:Array.Empty<Guid>());
            foreach(var sketch in sketches) {
                ids.Add(sketch,sketch);
                foreach(var role in sketch==plan.Steps[1].OutputEntityId.Value?CadParametricIdentity.Roles:CadParametricIdentity.HoleRoles.ToArray())
                    ids.Add(CadParametricIdentity.For(sourceId,sketch,role),CadParametricIdentity.For(child.ModelId,sketch,role));
            }
            return source.Select(binding=> {
                var copy=JObject.FromObject(binding).ToObject<CadEntityReferenceBinding>();
                if(copy.ModelId!=sourceId||copy.Status!=CadEntityReferenceStatus.Active||!ids.ContainsKey(copy.EntityId))throw new InvalidOperationException("Foreign source reference.");
                copy.ModelId=child.ModelId;copy.EntityId=ids[binding.EntityId];copy.CreatedAtModelRevisionId=child.CurrentModelRevisionId;copy.LastResolvedModelRevisionId=null;
                copy.CreatedUtc=copy.UpdatedUtc=DateTime.UtcNow;
                if(!string.IsNullOrWhiteSpace(copy.SemanticFingerprintJson)) {
                    var fingerprint=JObject.Parse(copy.SemanticFingerprintJson);
                    foreach(var p in fingerprint.Properties().ToList()) {
                        if(p.Value.Type!=JTokenType.String||!Guid.TryParse(p.Value.Value<string>(),out var id))continue;
                        if(id==sourceId)p.Value=child.ModelId.ToString("D");else if(ids.TryGetValue(id,out var replacement))p.Value=replacement.ToString("D");
                    }
                    copy.SemanticFingerprintJson=fingerprint.ToString(Formatting.None);
                }
                return copy;
            }).ToList();
        }
        private static JObject Check(ModelDoc2 document,CadModelIdentityRecord identity,CadPlanV2Document expected,IReadOnlyList<CadEntityReferenceBinding> bindings)
        {
            var planErrors=CadPlanV2Preflight.Validate(expected,true);
            if(planErrors.Count>0||CadParametricEditContract.IsEdit(expected)||!CadParametricPlateContract.IsParametric(expected))throw new InvalidOperationException("Exact expected creation geometry required.");
            var geometry=CadV2PrismaticGeometry.Analyze(expected);var plateIntent=CadParametricPlateContract.ReadIntent(expected);
            var sketch=expected.Steps[1].OutputEntityId.Value;
            var plate=ParametricPlateInspector.Inspect(document,identity,sketch,Subset(bindings,identity.ModelId,sketch,CadParametricIdentity.Roles));
            CheckParameters(plate,new Dictionary<string,double>{{"width",plateIntent.WidthMm},{"length",plateIntent.LengthMm},{"thickness",plateIntent.ThicknessMm}},4,1);
            CadParametricHoleObservation hole=null;
            if(CadParametricHoleContract.IsHole(expected)) {
                var holeSketch=expected.Steps[5].OutputEntityId.Value;var intent=CadParametricHoleContract.ReadIntent(expected);
                hole=ParametricHoleInspector.Inspect(document,identity,holeSketch,Subset(bindings,identity.ModelId,holeSketch,CadParametricIdentity.HoleRoles));
                CheckParameters(hole,new Dictionary<string,double>{{"holeDiameter",intent.DiameterMm},{"holeX",intent.Xmm},{"holeY",intent.Ymm}},1,0);
                if(!Close(hole.Geometry.Value<double>("diameterMm"),intent.DiameterMm,0.001)||!Close(hole.Geometry.Value<double>("centerXmm"),intent.Xmm,0.001)||!Close(hole.Geometry.Value<double>("centerYmm"),intent.Ymm,0.001))
                    throw new InvalidOperationException("Independent circle geometry differs from intent.");
            }
            var bodies=SolidWorksInspector.GetSolidBodies(document);
            if(bodies.Length!=1)throw new InvalidOperationException("Exactly one solid body required.");
            var mass=(double[])((Body2)bodies[0]).GetMassProperties(1);var volume=mass[3]*1e9;
            if(!Close(volume,geometry.VolumeMm3,Math.Max(0.05,geometry.VolumeMm3*0.0001)))throw new InvalidOperationException("Observed volume differs from edit intent.");
            var box=SolidWorksInspector.GetPreciseBoundingBox(document);
            var bounds=new JObject{{"minXmm",box.MinXmm},{"maxXmm",box.MaxXmm},{"minYmm",box.MinYmm},{"maxYmm",box.MaxYmm},{"minZmm",box.MinZmm},{"maxZmm",box.MaxZmm},{"sizeXmm",box.SizeXmm},{"sizeYmm",box.SizeYmm},{"sizeZmm",box.SizeZmm}};
            if(geometry.ExpectedBounds().Properties().Any(p=>!Close(bounds.Value<double>(p.Name),p.Value.Value<double>(),0.02)))throw new InvalidOperationException("Observed bounds differ from edit intent.");
            var cuts=SolidWorksInspector.GetPrismaticCutReadback(document);
            if(!CadV2NativeCutReadbackMatcher.Matches(geometry.Cuts,cuts,out var reason))throw new InvalidOperationException(reason);
            return CadCommandResult.Ok(new {parametricPlate=plate,parametricHole=hole,
                bodies=new {bodyCount=1,volumeMm3=volume},bounds,nativeCuts=cuts,hasRebuildErrors=false}).Data;
        }
        private static IReadOnlyList<CadEntityReferenceBinding> Subset(IReadOnlyList<CadEntityReferenceBinding> bindings,Guid model,Guid sketch,IEnumerable<string> roles)
        {
            var ids=new HashSet<Guid>(roles.Select(r=>CadParametricIdentity.For(model,sketch,r))){sketch};return bindings.Where(b=>ids.Contains(b.EntityId)).ToList();
        }
        private static void CheckParameters(CadParametricSketchObservation observed,Dictionary<string,double> values,int contour,int construction)
        {
            if(!observed.FullyDefined||!observed.RequiredRelationsPresent||observed.HasFixedContourEntities||observed.ContourEdgeCount!=contour||observed.ConstructionEdgeCount!=construction||observed.SolidBodyCount!=1||observed.RebuildErrorCount!=0||observed.Dimensions.Count!=values.Count||
                observed.Dimensions.Any(d=>!d.IsDriving||!values.ContainsKey(d.Role)||!Close(d.ValueMm,values[d.Role],0.001)))
                throw new InvalidOperationException("Native parameter constraints or values differ from expected intent.");
        }
        private static bool Close(double actual,double expected,double tolerance)=>!double.IsNaN(actual)&&!double.IsInfinity(actual)&&Math.Abs(actual-expected)<=tolerance;
#endif
    }
}
