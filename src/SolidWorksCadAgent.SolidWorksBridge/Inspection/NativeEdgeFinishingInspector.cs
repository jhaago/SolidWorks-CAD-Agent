// Adapted from soheil-mp/solidworks-mcp f450dab, Services/FeatureService.cs, PartFeatureService.cs and FeatureManagementService.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors. See third_party/solidworks-mcp/LICENSE.
// Changes: typed 2020 subset, strict bounded readback, no AccessSelections, no freeform/unmeasured success fallback.
#if SOLIDWORKS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    internal static class NativeEdgeFinishingInspector
    {
        internal static EdgeFinishingDefinition ReadDefinition(Feature feature)
        {
            string kind=NativeType(feature);
            if(kind=="Fillet")
            {
                var d=feature.GetDefinition() as SimpleFilletFeatureData2??throw new InvalidOperationException("A simple native fillet definition is required.");
                return Readable(new EdgeFinishingDefinition{Kind=EdgeFinishingKind.Fillet,Variant=d.Type==0?"constant_radius":"unsupported:"+d.Type,
                    SizeMm=d.DefaultRadius*1000,EdgeCount=d.GetEdgeCount(),TangentPropagation=d.PropagateToTangentFaces,
                    MultipleRadius=d.IsMultipleRadius,Asymmetric=d.AsymmetricFillet});
            }
            if(kind=="Chamfer")
            {
                var d=feature.GetDefinition() as ChamferFeatureData2??throw new InvalidOperationException("A native edge chamfer definition is required.");
                return Readable(new EdgeFinishingDefinition{Kind=EdgeFinishingKind.Chamfer,
                    Variant=d.Type==(int)swChamferType_e.swChamferAngleDistance?"angle_distance":"unsupported:"+d.Type,
                    SizeMm=d.GetEdgeChamferDistance(0)*1000,AngleDegrees=d.EdgeChamferAngle*180/Math.PI,EdgeCount=d.GetEdgeCount(),TangentPropagation=d.TangentPropagation});
            }
            throw new InvalidOperationException("The feature is not a supported native finishing type.");
        }
        private static EdgeFinishingDefinition Readable(EdgeFinishingDefinition definition)
        {
            if(!Vec3.Finite(definition.SizeMm)||definition.SizeMm<=0||definition.EdgeCount<1||definition.EdgeCount>20000||
                (definition.AngleDegrees.HasValue&&!Vec3.Finite(definition.AngleDegrees.Value)))
                throw new InvalidOperationException("Native finishing definition measurements are unavailable, nonfinite or outside their evidence budget.");
            return definition;
        }
        internal static string NativeType(Feature feature)
        {string type=feature.GetTypeName2();return type=="ICE"?feature.GetTypeName():type;}
        internal static IReadOnlyList<FinishingLine> CaptureOriginalEdges(IReadOnlyList<Edge> edges)
        {
            if(edges==null||edges.Count==0||edges.Count>256)throw new InvalidOperationException("A bounded outer edge set is required.");
            var result=new List<FinishingLine>();
            for(int i=0;i<edges.Count;i++)
            {
                var edge=edges[i];var line=ReadLine(edge,"outer:"+i);
                if(line==null)throw new InvalidOperationException("The first finishing ports support straight outer edges only.");
                var adjacent=edge.GetTwoAdjacentFaces2() as object[];
                if(adjacent==null||adjacent.Length!=2)throw new InvalidOperationException("Each finishing edge needs two adjacent planar faces.");
                var normals=new List<Vec3>();
                foreach(var item in adjacent)
                {
                    var face=item as Face2??throw new InvalidOperationException("Native adjacent face is unavailable.");
                    var surface=face.GetSurface() as Surface;
                    if(surface==null||!surface.IsPlane())throw new InvalidOperationException("Finishing requires planar adjacent faces.");
                    normals.Add(Vec3.From(Values(face.Normal,3,"adjacent normal")).Normalized());
                }
                if(Math.Abs(normals[0].Dot(normals[1]))>1e-8)throw new InvalidOperationException("The bounded finishing ports require orthogonal adjacent faces.");
                result.Add(line);
            }
            return result;
        }
        internal static IReadOnlyList<double> RoundRadiiMm(Feature feature)
        {
            var result=new List<double>();
            foreach(var face in Faces(feature))
            {
                var s=face.GetSurface() as Surface??throw new InvalidOperationException("Native fillet surface is unavailable.");
                if(s.IsPlane())continue;
                if(s.IsCylinder())result.Add(Values(s.CylinderParams,7,"cylinder")[6]*1000);
                else if(s.IsTorus())result.Add(Values(s.TorusParams,8,"torus")[7]*1000);
                else if(s.IsSphere())result.Add(Values(s.SphereParams,4,"sphere")[3]*1000);
                else throw new InvalidOperationException("Freeform fillet-face radius verification is unsupported; no unmeasured success is accepted.");
            }
            return result;
        }
        internal static IReadOnlyList<IReadOnlyList<FinishingLine>> ChamferFaces(Feature feature)
        {
            var result=new List<IReadOnlyList<FinishingLine>>();int visits=0;
            foreach(var face in Faces(feature))
            {
                var s=face.GetSurface() as Surface;
                if(s==null||!s.IsPlane())throw new InvalidOperationException("Only planar chamfer-face verification is supported.");
                var objects=TopologyArrayEvidence.RequireComplete(face.GetEdges() as object[],face.GetEdgeCount(),20000-visits,"chamfer-face edges");visits+=objects.Length;
                var lines=new List<FinishingLine>();
                for(int i=0;i<objects.Length;i++)
                {
                    var edge=objects[i] as Edge??throw new InvalidOperationException("Native chamfer edge is unavailable.");
                    var line=ReadLine(edge,"face:"+result.Count+":edge:"+i);if(line!=null)lines.Add(line);
                }
                if(lines.Count>0)result.Add(lines);
            }
            return result;
        }
        internal static object[] InspectDefinitions(ModelDoc2 model)
        {
            var part=model as PartDoc??throw new InvalidOperationException("Finishing definition inspection supports part documents only.");
            var tree=SolidWorksInspector.GetFeatureTree(model);
            FeatureInventory.Capture(tree.Select(f=>new KeyValuePair<string,string>(f.Name,f.TypeName)));
            return tree.Where(f=>f.TypeName=="Fillet"||f.TypeName=="Chamfer").Select(item=>
            {
                var feature=part.FeatureByName(item.Name) as Feature;
                if(feature==null||feature.Name!=item.Name||NativeType(feature)!=item.TypeName)throw new InvalidOperationException("A unique native finishing feature could not be resolved for inspection.");
                return (object)new{featureName=item.Name,nativeType=item.TypeName,definition=ReadDefinition(feature),geometryVerified=false,
                    selectionReadbackScope="stored definition edge count; no AccessSelections, selection or rebuild"};
            }).ToArray();
        }
        private static IEnumerable<Face2> Faces(Feature feature)
        {
            var objects=feature.GetFaces() as object[];
            if(objects==null||objects.Length==0||objects.Length>20000)throw new InvalidOperationException("Native finishing faces are unavailable or exceed their budget.");
            foreach(var item in objects)yield return item as Face2??throw new InvalidOperationException("Native finishing face is unavailable.");
        }
        private static FinishingLine ReadLine(Edge edge,string key)
        {
            var curve=edge.GetCurve() as Curve??throw new InvalidOperationException("Native edge curve is unavailable.");
            if(!curve.IsLine())return null;
            var p=edge.GetCurveParams3() as CurveParamData??throw new InvalidOperationException("Native edge parameters are unavailable.");
            var start=Vec3.From(Values(p.StartPoint,3,"line start"))*1000;var end=Vec3.From(Values(p.EndPoint,3,"line end"))*1000;
            return new FinishingLine{Key=key,PointMm=start,Direction=(end-start).Normalized()};
        }
        private static double[] Values(object native,int count,string label)
        {
            var values=native as double[];
            if(values==null||values.Length!=count||values.Any(v=>!Vec3.Finite(v)))throw new InvalidOperationException("Native "+label+" evidence is incomplete or nonfinite.");
            return values;
        }
    }
}
#endif
