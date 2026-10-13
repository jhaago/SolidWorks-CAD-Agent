using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
#endif
namespace SolidWorksCadAgent.SolidWorksBridge.Parametric
{
    public static class ParametricPlateInspector
    {
        public static CadParametricPlateObservation Inspect(object document,CadModelIdentityRecord identity,Guid sketchId,IReadOnlyList<CadEntityReferenceBinding> bindings)
        {
#if SOLIDWORKS_INTEROP
            var model=(ModelDoc2)document;
            if(identity==null||identity.ModelId==Guid.Empty||model.ConfigurationManager.ActiveConfiguration.Name!=identity.ConfigurationKey)
                throw new InvalidOperationException("Native parametric configuration mismatch.");
            if(bindings==null||bindings.Count!=10)throw new InvalidOperationException("Complete parametric role and sketch bindings required.");
            var sketchBinding=bindings.Single(b=>b.EntityId==sketchId);
            var resolvedSketch=Resolve(model,sketchBinding);
            var sketch=resolvedSketch as Sketch ?? ((Feature)resolvedSketch).GetSpecificFeature2() as Sketch;
            if(sketch==null)throw new InvalidOperationException("Captured sketch no longer resolves.");
            var segments=(sketch.GetSketchSegments() as object[] ??new object[0]).Cast<SketchSegment>().ToArray();
            var result=new CadParametricPlateObservation {EvidenceVersion=1,ModelId=identity.ModelId,SketchEntityId=sketchId,ConfigurationKey=identity.ConfigurationKey,
                FullyDefined=sketch.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained,
                ContourEdgeCount=segments.Count(s=>!s.ConstructionGeometry),ConstructionEdgeCount=segments.Count(s=>s.ConstructionGeometry),
                SolidBodyCount=SolidWorksInspector.GetSolidBodies(model).Length,
                RebuildErrorCount=SolidWorksInspector.GetFeatureTree(model).Count(f=>f.ErrorCode!=0&&!f.IsWarning)};
            var types=(sketch.RelationManager.GetRelations(0) as object[] ??new object[0]).Cast<SketchRelation>().Select(r=>r.GetRelationType()).ToArray();
            result.RequiredRelationsPresent=types.Count(t=>t==(int)swConstraintType_e.swConstraintType_HORIZONTAL)==2&&
                types.Count(t=>t==(int)swConstraintType_e.swConstraintType_VERTICAL)==2&&types.Contains((int)swConstraintType_e.swConstraintType_ATMIDDLE);
            result.HasFixedContourEntities=types.Contains((int)swConstraintType_e.swConstraintType_FIXED);
            Feature boss=null;
            foreach(var role in CadParametricIdentity.Roles)
            {
                var binding=bindings.Single(b=>b.EntityId==CadParametricIdentity.For(identity.ModelId,sketchId,role));
                if(binding.ModelId!=identity.ModelId||binding.ConfigurationKey!=identity.ConfigurationKey||binding.Status!=CadEntityReferenceStatus.Active||binding.ReferenceFormatVersion!=3)
                    throw new InvalidOperationException("Parametric role ownership or reference format differs.");
                var fingerprint=JObject.Parse(binding.SemanticFingerprintJson);
                if(fingerprint.Value<int>("evidenceVersion")!=1||fingerprint.Value<string>("role")!=role||fingerprint["modelId"]?.ToObject<Guid>()!=identity.ModelId||fingerprint["sketchEntityId"]?.ToObject<Guid>()!=sketchId||fingerprint.Value<string>("configurationKey")!=identity.ConfigurationKey)
                    throw new InvalidOperationException("Parametric role fingerprint differs.");
                var owner=Resolve(model,binding);
                if(role=="boss")
                {
                    boss=owner as Feature;
                    var type=boss?.GetTypeName2();if(type=="ICE")type=boss.GetTypeName();
                    var definition=boss?.GetDefinition() as ExtrudeFeatureData2;
                    if(boss==null||(type!="Boss"&&type!="Extrusion")||definition==null||(!definition.IsBossFeature()&&!boss.IsBase2()))
                        throw new InvalidOperationException("Captured base extrusion or boss definition is missing or of another kind: "+type);
                    if(!definition.AccessSelections(model,null))throw new InvalidOperationException("Native extrusion definition selections unavailable.");
                    try
                    {
                        if(definition.GetEndCondition(true)!=(int)swEndConditions_e.swEndCondBlind||definition.IsThinFeature()||definition.BothDirections)
                            throw new InvalidOperationException("Only a one-direction non-thin blind base extrusion is supported.");
                    }
                    finally{definition.ReleaseSelectionAccess();}
                    continue;
                }
                if(role!="width"&&role!="length"&&role!="thickness")
                {
                    if(!(owner is SketchSegment segment)||!segments.Any(s=>Same(s,segment))||segment.ConstructionGeometry!=(role=="centreDiagonal"))
                        throw new InvalidOperationException("Captured profile segment is missing or has another contour role.");
                    continue;
                }
                var feature=owner as Feature;
                if(feature==null||binding.NativeObjectKind!="DimensionOwnerFeature")throw new InvalidOperationException("Captured dimension owner is missing.");
                if(role!="thickness"&&!Same(feature.GetSpecificFeature2(),sketch))throw new InvalidOperationException("Dimension belongs to a foreign sketch.");
                if(role=="thickness" && (boss==null||!Same(feature,boss)))
                {
                    // Role order places thickness before boss; resolve its independently captured boss owner.
                    var bossBinding=bindings.Single(b=>b.EntityId==CadParametricIdentity.For(identity.ModelId,sketchId,"boss"));
                    if(!Same(feature,Resolve(model,bossBinding)))throw new InvalidOperationException("Thickness belongs to a foreign boss.");
                }
                var name=fingerprint.Value<string>("nativeDimensionName");
                var expectedName=role=="width"?"AgentWidth":role=="length"?"AgentLength":"AgentThickness";
                if(name!=expectedName)throw new InvalidOperationException("Captured semantic dimension name differs.");
                var dimensions=new List<Dimension>();
                for(var display=(DisplayDimension)feature.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)feature.GetNextDisplayDimension(display))
                {
                    var dimension=(Dimension)display.GetDimension2(0);
                    if(dimension.FullName.Split('@')[0]==name)dimensions.Add(dimension);
                }
                if(dimensions.Count!=1)throw new InvalidOperationException("Named driving dimension missing or duplicated.");
                var native=dimensions.Single();
                var ownerId=role=="thickness"?CadParametricIdentity.For(identity.ModelId,sketchId,"boss"):sketchId;
                if(fingerprint["ownerEntityId"]?.ToObject<Guid>()!=ownerId)throw new InvalidOperationException("Captured dimension owner identity differs.");
                result.Dimensions.Add(new CadParametricDimensionObservation {EntityId=binding.EntityId,Role=role,OwnerEntityId=ownerId,
                    ValueMm=native.GetSystemValue2(identity.ConfigurationKey)*1000,IsDriving=native.DrivenState==(int)swDimensionDrivenState_e.swDimensionDriving});
            }
            return result;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
#if SOLIDWORKS_INTEROP
        private static object Resolve(ModelDoc2 model,CadEntityReferenceBinding binding)
        {
            if(binding?.NativeReferenceBytes==null||binding.Status!=CadEntityReferenceStatus.Active)throw new InvalidOperationException("Active captured native owner required.");
            int status;var value=model.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes,out status);
            if(status!=0||value==null)throw new InvalidOperationException("Captured native reference is stale, suppressed or deleted.");return value;
        }
        private static bool Same(object a,object b)
        {
            if(a==null||b==null)return false;var x=Marshal.GetIUnknownForObject(a);var y=Marshal.GetIUnknownForObject(b);
            try{return x==y;}finally{Marshal.Release(x);Marshal.Release(y);}
        }
#endif
    }
}
