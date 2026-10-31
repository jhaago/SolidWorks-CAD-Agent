// Ported in part from soheil-mp/solidworks-mcp f450dab, Geometry/Topology.cs and Com/SwExt.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Part-only, old APIs, no COM objects leave the STA.
#if SOLIDWORKS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SolidWorks.Interop.sldworks;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.Core.Interop;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    internal static class NativeTopologyReader
    {
        internal static IReadOnlyList<TopologyFact> Read(ModelDoc2 model,string kind)
        {
            var result=new List<TopologyFact>();
            foreach(var bodyObject in SolidWorksInspector.GetSolidBodies(model))
            {
                var body=bodyObject as Body2??throw new InvalidOperationException("Native solid body unavailable.");
                var objects=(kind=="Face"?body.GetFaces():body.GetEdges()) as object[];
                if(objects==null)throw new InvalidOperationException("Native topology enumeration unavailable.");
                if(objects.Length>20000-result.Count)throw new InvalidOperationException("Topology exceeds the 20,000-entity inspection budget.");
                foreach(var entity in objects)
                {
                    var fact=kind=="Face"?ReadFace((Face2)entity):ReadEdge((Edge)entity);
                    var bytes=model.Extension.GetPersistReference3(entity) as byte[];
                    if(bytes==null||bytes.Length==0||bytes.Length>1024*1024)throw new InvalidOperationException("Topology reference unavailable.");
                    using(var hash=SHA256.Create())fact.InspectionId=kind+":"+BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                    fact.BodyName=body.Name;result.Add(fact);
                }
            }
            return result;
        }
        private static TopologyFact ReadFace(Face2 face)
        {
            var surface=face.GetSurface() as Surface??throw new InvalidOperationException("Native surface unavailable.");
            var box=Values(face.GetBox(),6,"face box");
            var fact=new TopologyFact{Kind="Face",Geometry=SurfaceKind(surface),AreaMm2=face.GetArea()*1e6,
                PositionMm=new Vec3((box[0]+box[3])*500,(box[1]+box[4])*500,(box[2]+box[5])*500),
                PositionMethod="approximate_face_box_center",FeatureName=(face.GetFeature() as Feature)?.Name};
            if(surface.IsPlane())fact.Normal=Vec3.From(Values(face.Normal,3,"face normal")).Normalized();
            else if(surface.IsCylinder()||surface.IsCone()||surface.IsTorus())
            {
                object native=surface.IsCylinder()?surface.CylinderParams:surface.IsCone()?surface.ConeParams:surface.TorusParams;
                var p=Values(native,7,"surface parameters");fact.Direction=Vec3.From(p,3).Normalized();fact.RadiusMm=p[6]*1000;
            }
            else if(surface.IsSphere())fact.RadiusMm=Values(surface.SphereParams,4,"sphere parameters")[3]*1000;
            return fact;
        }
        private static TopologyFact ReadEdge(Edge edge)
        {
            var curve=edge.GetCurve() as Curve??throw new InvalidOperationException("Native curve unavailable.");
            // GetCurveParams3 exists since SOLIDWORKS 2011. Avoid GetCurveParams2's packed/signed parameter encoding.
            var parameters=edge.GetCurveParams3() as CurveParamData??throw new InvalidOperationException("Native edge parameters unavailable.");
            var start=Vec3.From(Values(parameters.StartPoint,3,"edge start"));
            var end=Vec3.From(Values(parameters.EndPoint,3,"edge end"));
            double minimum=parameters.UMinValue,maximum=parameters.UMaxValue;
            var middle=NativeEdgeEvaluation.ReadPositionMetres(edge.Evaluate2(NativeEdgeEvaluation.ParameterMidpoint(minimum,maximum),0) as double[]);
            var fact=new TopologyFact{Kind="Edge",Geometry=CurveKind(curve),StartMm=start*1000,EndMm=end*1000,
                PositionMm=middle*1000,PositionMethod="native_curve_parameter_midpoint",LengthMm=curve.GetLength3(minimum,maximum)*1000};
            fact.FeatureNames=(edge.GetTwoAdjacentFaces2() as object[]??Array.Empty<object>()).OfType<Face2>()
                .Select(face=>(face.GetFeature() as Feature)?.Name).Where(name=>!string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
            if(curve.IsLine())fact.Direction=(end-start).Normalized();
            else if(curve.IsCircle())
            {var circle=Values(curve.CircleParams,7,"circle parameters");fact.Direction=Vec3.From(circle,3).Normalized();fact.RadiusMm=circle[6]*1000;}
            return fact;
        }
        private static string SurfaceKind(Surface s)=>s.IsPlane()?"plane":s.IsCylinder()?"cylinder":s.IsCone()?"cone":s.IsSphere()?"sphere":s.IsTorus()?"torus":s.IsParametric()?"bspline":"other";
        private static string CurveKind(Curve c)=>c.IsLine()?"line":c.IsCircle()?"circle":c.IsEllipse()?"ellipse":c.IsBcurve()?"bspline":"other";
        private static double[] Values(object value,int minimum,string label)
        {
            var values=value as double[];
            if(values==null||values.Length<minimum)throw new InvalidOperationException("Native "+label+" unavailable.");
            // GetCurveParams2 has packed metadata after its first eight geometric values.
            for(int i=0;i<minimum;i++)if(!Vec3.Finite(values[i]))throw new InvalidOperationException("Native "+label+" is nonfinite.");
            return values;
        }
    }
}
#endif
