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
            var edges=ValidateEdgeRoles(model,identity.ModelId,sketchId,bindings,segments);
            var result=new CadParametricPlateObservation {EvidenceVersion=1,ModelId=identity.ModelId,SketchEntityId=sketchId,ConfigurationKey=identity.ConfigurationKey,
                FullyDefined=sketch.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained,
                ContourEdgeCount=segments.Count(s=>!s.ConstructionGeometry),ConstructionEdgeCount=segments.Count(s=>s.ConstructionGeometry),
                SolidBodyCount=SolidWorksInspector.GetSolidBodies(model).Length,
                RebuildErrorCount=SolidWorksInspector.GetFeatureTree(model).Count(f=>f.ErrorCode!=0&&!f.IsWarning)};
            var relations=(sketch.RelationManager.GetRelations(0) as object[] ??new object[0]).Cast<SketchRelation>().ToArray();
            var types=relations.Select(r=>r.GetRelationType()).ToArray();
            result.RequiredRelationsPresent=types.Count(t=>t==(int)swConstraintType_e.swConstraintType_HORIZONTAL)==2&&
                types.Count(t=>t==(int)swConstraintType_e.swConstraintType_VERTICAL)==2&&
                HasRelation(model,relations,swConstraintType_e.swConstraintType_HORIZONTAL,edges["bottom"])&&
                HasRelation(model,relations,swConstraintType_e.swConstraintType_HORIZONTAL,edges["top"])&&
                HasRelation(model,relations,swConstraintType_e.swConstraintType_VERTICAL,edges["right"])&&
                HasRelation(model,relations,swConstraintType_e.swConstraintType_VERTICAL,edges["left"])&&
                HasRelation(model,relations,swConstraintType_e.swConstraintType_ATMIDDLE,ParametricRectangleBuilder.Origin(model),edges["centreDiagonal"]);
            if(!result.RequiredRelationsPresent)
                throw new InvalidOperationException("Native semantic relation associations differ: "+string.Join(",",new[]{
                    HasRelation(model,relations,swConstraintType_e.swConstraintType_HORIZONTAL,edges["bottom"]),
                    HasRelation(model,relations,swConstraintType_e.swConstraintType_HORIZONTAL,edges["top"]),
                    HasRelation(model,relations,swConstraintType_e.swConstraintType_VERTICAL,edges["right"]),
                    HasRelation(model,relations,swConstraintType_e.swConstraintType_VERTICAL,edges["left"]),
                    HasRelation(model,relations,swConstraintType_e.swConstraintType_ATMIDDLE,ParametricRectangleBuilder.Origin(model),edges["centreDiagonal"])}));
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
                    if(!(owner is SketchSegment segment)||!segments.Any(s=>SameNative(model,s,segment))||segment.ConstructionGeometry!=(role=="centreDiagonal"))
                        throw new InvalidOperationException("Captured profile segment is missing or has another contour role.");
                    continue;
                }
                var feature=owner as Feature;
                if(feature==null||binding.NativeObjectKind!="DimensionOwnerFeature")throw new InvalidOperationException("Captured dimension owner is missing.");
                if(role!="thickness"&&!SameNative(model,feature.GetSpecificFeature2(),sketch))throw new InvalidOperationException("Dimension belongs to a foreign sketch.");
                if(role=="thickness" && (boss==null||!SameNative(model,feature,boss)))
                {
                    // Role order places thickness before boss; resolve its independently captured boss owner.
                    var bossBinding=bindings.Single(b=>b.EntityId==CadParametricIdentity.For(identity.ModelId,sketchId,"boss"));
                    if(!SameNative(model,feature,Resolve(model,bossBinding)))throw new InvalidOperationException("Thickness belongs to a foreign boss.");
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
                if(role!="thickness")ValidateDimensionAttachment(model,relations,native,edges[role=="width"?"bottom":"right"]);
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
        internal static object Resolve(ModelDoc2 model,CadEntityReferenceBinding binding)
        {
            if(binding?.NativeReferenceBytes==null||binding.Status!=CadEntityReferenceStatus.Active)throw new InvalidOperationException("Active captured native owner required.");
            int status;var value=model.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes,out status);
            if(status!=0||value==null)throw new InvalidOperationException("Captured native reference is stale, suppressed or deleted.");return value;
        }
        private static Dictionary<string,SketchLine> ValidateEdgeRoles(ModelDoc2 model,Guid modelId,Guid sketchId,IReadOnlyList<CadEntityReferenceBinding> bindings,SketchSegment[] segments)
        {
            var names=new[]{"bottom","right","top","left","centreDiagonal"};
            var edges=new Dictionary<string,SketchLine>();
            foreach(var role in names)
            {
                var line=Resolve(model,bindings.Single(b=>b.EntityId==CadParametricIdentity.For(modelId,sketchId,role))) as SketchLine;
                if(line==null||!segments.Any(s=>SameNative(model,s,line))||((SketchSegment)line).ConstructionGeometry!=(role=="centreDiagonal")||edges.Values.Any(v=>SameNative(model,v,line)))
                    throw new InvalidOperationException("Native semantic edge is missing, aliased or of another kind: "+role);
                edges.Add(role,line);
            }
            var points=edges.Where(e=>e.Key!="centreDiagonal").SelectMany(e=>new[]{(SketchPoint)e.Value.GetStartPoint2(),(SketchPoint)e.Value.GetEndPoint2()}).ToArray();
            double minX=points.Min(p=>p.X),maxX=points.Max(p=>p.X),minY=points.Min(p=>p.Y),maxY=points.Max(p=>p.Y);
            if(points.Any(p=>!Finite(p.X)||!Finite(p.Y)||!Close(p.Z,0))||maxX<=minX||maxY<=minY||!Close(minX+maxX,0)||!Close(minY+maxY,0)||
                !Endpoints(edges["bottom"],minX,minY,maxX,minY)||!Endpoints(edges["right"],maxX,minY,maxX,maxY)||
                !Endpoints(edges["top"],minX,maxY,maxX,maxY)||!Endpoints(edges["left"],minX,minY,minX,maxY)||
                !Endpoints(edges["centreDiagonal"],minX,minY,maxX,maxY))
                throw new InvalidOperationException("Native edge coordinates differ from their captured rectangle roles.");
            return edges;
        }
        private static bool Endpoints(SketchLine line,double x1,double y1,double x2,double y2)
        {
            var a=(SketchPoint)line.GetStartPoint2();var b=(SketchPoint)line.GetEndPoint2();
            return Close(a.Z,0)&&Close(b.Z,0)&&
                (Close(a.X,x1)&&Close(a.Y,y1)&&Close(b.X,x2)&&Close(b.Y,y2)||Close(b.X,x1)&&Close(b.Y,y1)&&Close(a.X,x2)&&Close(a.Y,y2));
        }
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        private static bool Close(double a,double b)=>Finite(a)&&Finite(b)&&Math.Abs(a-b)<=1e-8;
        private static bool HasRelation(ModelDoc2 model,SketchRelation[] relations,swConstraintType_e type,params object[] expected)=>
            relations.Any(r=>r.GetRelationType()==(int)type&&
                (ContainsExactly(model,r.GetDefinitionEntities2() as object[],expected)||ContainsExactly(model,r.GetEntities() as object[],expected)));
        internal static bool ContainsExactly(ModelDoc2 model,object[] entities,object[] expected)=>entities!=null&&entities.Length==expected.Length&&expected.All(e=>entities.Any(n=>SameNative(model,e,n)));
        private static void ValidateDimensionAttachment(ModelDoc2 model,SketchRelation[] relations,Dimension dimension,SketchLine edge)
        {
            var matches=relations.Where(r=>r.GetDisplayDimension() is DisplayDimension display&&
                display.GetDimension2(0) is Dimension native&&native.FullName==dimension.FullName).ToArray();
            var points=new object[]{edge.GetStartPoint2(),edge.GetEndPoint2()};
            if(matches.Length!=1||
                !(ContainsExactly(model,matches[0].GetEntities() as object[],points)||
                  ContainsExactly(model,matches[0].GetDefinitionEntities2() as object[],points)||
                  ContainsExactly(model,matches[0].GetDefinitionEntities2() as object[],new object[]{edge})))
                throw new InvalidOperationException("Named dimension controls a different native edge or direction: "+dimension.FullName);
        }
        internal static bool SameNative(ModelDoc2 model,object a,object b)
        {
            if(Same(a,b))return true;if(a==null||b==null)return false;
            var first=model.Extension.GetPersistReference3(a) as byte[];var second=model.Extension.GetPersistReference3(b) as byte[];
            return first?.Length>0&&second?.Length>0&&first.SequenceEqual(second);
        }
        private static bool Same(object a,object b)
        {
            if(a==null||b==null)return false;var x=Marshal.GetIUnknownForObject(a);var y=Marshal.GetIUnknownForObject(b);
            try{return x==y;}finally{Marshal.Release(x);Marshal.Release(y);}
        }
#endif
    }
}
