using System;
using System.Collections.Generic;
using System.Linq;
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
    public static class ParametricHoleInspector
    {
        public static CadParametricHoleObservation Inspect(object document,CadModelIdentityRecord identity,Guid sketchId,IReadOnlyList<CadEntityReferenceBinding> bindings)
        {
#if SOLIDWORKS_INTEROP
            var model=(ModelDoc2)document;
            if(identity==null||identity.ModelId==Guid.Empty||sketchId==Guid.Empty||model.ConfigurationManager.ActiveConfiguration.Name!=identity.ConfigurationKey||bindings?.Count!=5)
                throw new InvalidOperationException("Complete native hole ownership and configuration required.");
            var original=bindings.Single(b=>b.EntityId==sketchId);
            if(original.ModelId!=identity.ModelId||original.ConfigurationKey!=identity.ConfigurationKey)throw new InvalidOperationException("Foreign hole sketch binding.");
            var nativeSketch=ParametricPlateInspector.Resolve(model,original);
            var sketch=nativeSketch as Sketch??(nativeSketch as Feature)?.GetSpecificFeature2() as Sketch;
            if(sketch==null)throw new InvalidOperationException("Captured hole sketch missing.");
            var segments=(sketch.GetSketchSegments() as object[]??new object[0]).Cast<SketchSegment>().ToArray();
            var roles=new Dictionary<string,object>();var fingerprints=new Dictionary<string,JObject>();
            foreach(var role in CadParametricIdentity.HoleRoles)
            {
                var binding=bindings.Single(b=>b.EntityId==CadParametricIdentity.For(identity.ModelId,sketchId,role));
                if(binding.ModelId!=identity.ModelId||binding.ConfigurationKey!=identity.ConfigurationKey||binding.Status!=CadEntityReferenceStatus.Active||binding.ReferenceFormatVersion!=3)
                    throw new InvalidOperationException("Hole role ownership or reference format differs.");
                var f=JObject.Parse(binding.SemanticFingerprintJson);
                if(f.Value<int>("evidenceVersion")!=1||f.Value<string>("role")!=role||f["modelId"]?.ToObject<Guid>()!=identity.ModelId||
                    f["sketchEntityId"]?.ToObject<Guid>()!=sketchId||f.Value<string>("configurationKey")!=identity.ConfigurationKey||f["ownerEntityId"]?.ToObject<Guid>()!=sketchId)
                    throw new InvalidOperationException("Hole semantic fingerprint differs.");
                roles.Add(role,ParametricPlateInspector.Resolve(model,binding));fingerprints.Add(role,f);
                if(binding.NativeObjectKind!=(role=="holeCircle"?"SketchSegment":"DimensionOwnerFeature"))throw new InvalidOperationException("Hole role owner kind differs.");
            }
            var circle=roles["holeCircle"] as SketchArc;
            if(circle==null||circle.IsCircle()!=1||segments.Length!=1||segments[0].ConstructionGeometry||!ParametricPlateInspector.SameNative(model,segments[0],circle))
                throw new InvalidOperationException("Captured hole must be the sole non-construction full circle.");
            var centre=(SketchPoint)circle.GetCenterPoint2();var origin=ParametricRectangleBuilder.Origin(model);
            var relations=(sketch.RelationManager.GetRelations(0) as object[]??new object[0]).Cast<SketchRelation>().ToArray();
            var result=new CadParametricHoleObservation{EvidenceVersion=1,ModelId=identity.ModelId,SketchEntityId=sketchId,ConfigurationKey=identity.ConfigurationKey,
                FullyDefined=sketch.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained,RequiredRelationsPresent=true,
                HasFixedContourEntities=relations.Any(r=>r.GetRelationType()==(int)swConstraintType_e.swConstraintType_FIXED),ContourEdgeCount=1,ConstructionEdgeCount=0,
                SolidBodyCount=SolidWorksInspector.GetSolidBodies(model).Length,RebuildErrorCount=SolidWorksInspector.GetFeatureTree(model).Count(f=>f.ErrorCode!=0&&!f.IsWarning),
                Geometry=new JObject{["centerXmm"]=centre.X*1000,["centerYmm"]=centre.Y*1000,["diameterMm"]=circle.GetRadius()*2000}};
            foreach(var role in new[]{"holeDiameter","holeX","holeY"})
            {
                var feature=roles[role] as Feature;
                if(feature==null||!ParametricPlateInspector.SameNative(model,feature.GetSpecificFeature2(),sketch))throw new InvalidOperationException("Hole dimension belongs to another native sketch.");
                var name=role=="holeDiameter"?"AgentHoleDiameter":role=="holeX"?"AgentHoleX":"AgentHoleY";
                if(fingerprints[role].Value<string>("nativeDimensionName")!=name)throw new InvalidOperationException("Hole dimension name differs.");
                var displays=new List<DisplayDimension>();
                for(var d=(DisplayDimension)feature.GetFirstDisplayDimension();d!=null;d=(DisplayDimension)feature.GetNextDisplayDimension(d))displays.Add(d);
                if(displays.Count!=3)throw new InvalidOperationException("Exactly three native hole dimensions required.");
                var matches=displays.Where(d=>((Dimension)d.GetDimension2(0)).FullName.Split('@')[0]==name).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("Named hole dimension missing or duplicated.");
                var display=matches[0];var dim=(Dimension)display.GetDimension2(0);
                var expected=role=="holeDiameter"?swDimensionType_e.swDiameterDimension:role=="holeX"?swDimensionType_e.swHorLinearDimension:swDimensionType_e.swVertLinearDimension;
                if(display.Type2!=(int)expected)throw new InvalidOperationException("Hole dimension native direction/type differs: "+role+" type="+display.Type2);
                var dimensionRelations=relations.Where(r=>r.GetDisplayDimension() is DisplayDimension rd&&((Dimension)rd.GetDimension2(0)).FullName==dim.FullName).ToArray();
                var entities=role=="holeDiameter"?new object[]{circle}:new object[]{origin,centre};
                if(dimensionRelations.Length!=1||!(ParametricPlateInspector.ContainsExactly(model,dimensionRelations[0].GetEntities() as object[],entities)||
                    ParametricPlateInspector.ContainsExactly(model,dimensionRelations[0].GetDefinitionEntities2() as object[],entities)))
                    throw new InvalidOperationException("Hole dimension attaches to different native entities: "+role);
                result.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(identity.ModelId,sketchId,role),OwnerEntityId=sketchId,
                    Role=role,ValueMm=dim.GetSystemValue2(identity.ConfigurationKey)*1000,IsDriving=dim.DrivenState==(int)swDimensionDrivenState_e.swDimensionDriving});
            }
            return result;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
    }
}
