// Independently implemented against documented APIs available before SOLIDWORKS 2020.
// Native objects stay inside the bound-document STA operation; public hashes are inspection evidence only.
#if SOLIDWORKS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    internal static class NativeFaceSelector
    {
        private sealed class Choice { internal Face2 Face;internal PlanarFaceFact Fact;internal double Min,Max; }
        private sealed class Boundary { internal IReadOnlyList<Edge> Edges;internal FaceBoundaryLoop Outer;internal int LoopCount; }
        internal static Face2 Resolve(ModelDoc2 model,string axis,string side,CancellationToken token=default(CancellationToken))=>ResolveChoice(model,axis,side,token).Face;
        internal static IReadOnlyList<Edge> OuterEdges(ModelDoc2 model,Face2 face,CancellationToken token=default(CancellationToken))=>ReadBoundary(model,face,token).Edges;
        internal static object Inspect(ModelDoc2 model,string axis,string side,CancellationToken token)
        {
            var choice=ResolveChoice(model,axis,side,token);var boundary=ReadBoundary(model,choice.Face,token);
            return new{normalAxis=axis,side,unit="mm",faceKey=choice.Fact.Key,outwardNormal=choice.Fact.Normal,pointMm=choice.Fact.PointMm,
                bodyMinMm=choice.Min,bodyMaxMm=choice.Max,coordinateToleranceMm=AxisAlignedFaceSelection.CoordinateToleranceMm,
                outerEdgeCount=boundary.Edges.Count,innerLoopCount=boundary.LoopCount-1,outerEdgeKeys=boundary.Outer.EdgeKeys,
                pointSource="IFace2.GetClosestPointOn XYZUV",boundsSource="IBody2.GetExtremePoint",
                inspectionOnly=true,selectionAuthority=false,nativeCreated=false};
        }
        private static Choice ResolveChoice(ModelDoc2 model,string axis,string side,CancellationToken token)
        {
            if(axis!="X"&&axis!="Y"&&axis!="Z"||side!="Min"&&side!="Max")throw new ArgumentException("Invalid face axis or side.");
            token.ThrowIfCancellationRequested();
            var bodies=SolidWorksInspector.GetSolidBodies(model);
            if(bodies.Length!=1)throw new TopologyQueryException("Exact face selection requires one solid body, including hidden bodies; found "+bodies.Length+".");
            var body=bodies[0] as Body2??throw new TopologyQueryException("Native solid body is unavailable.");
            var u=axis=="X"?Vec3.UnitX:axis=="Y"?Vec3.UnitY:Vec3.UnitZ;
            if(!body.GetExtremePoint(u.X,u.Y,u.Z,out var x1,out var y1,out var z1)||!body.GetExtremePoint(-u.X,-u.Y,-u.Z,out var x0,out var y0,out var z0))
                throw new TopologyQueryException("Exact native body extents are unavailable.");
            double min=new Vec3(x0,y0,z0).Dot(u)*1000,max=new Vec3(x1,y1,z1).Dot(u)*1000;
            var nativeFaces=TopologyArrayEvidence.RequireComplete(body.GetFaces() as object[],body.GetFaceCount(),20000,"faces");
            var facts=new List<PlanarFaceFact>();var nativeById=new Dictionary<string,Face2>(StringComparer.Ordinal);
            foreach(var item in nativeFaces)
            {
                token.ThrowIfCancellationRequested();
                var face=item as Face2??throw new TopologyQueryException("Native face enumeration contains an invalid object.");
                var surface=face.GetSurface() as Surface??throw new TopologyQueryException("A native surface is unavailable.");
                if(!surface.IsPlane())continue;
                string key=Identity(model,face,"Face");
                if(nativeById.ContainsKey(key))throw new TopologyQueryException("Native face identity is duplicated.");
                facts.Add(PlanarFaceFact.FromNative(key,face.Normal as double[],face.GetClosestPointOn(0,0,0) as double[]));nativeById.Add(key,face);
            }
            var selected=AxisAlignedFaceSelection.Choose(facts,axis,side,min,max);
            return new Choice{Face=nativeById[selected.Key],Fact=selected,Min=min,Max=max};
        }
        private static Boundary ReadBoundary(ModelDoc2 model,Face2 face,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var objects=TopologyArrayEvidence.RequireComplete(face.GetLoops() as object[],face.GetLoopCount(),4096,"face loops");
            var facts=new List<FaceBoundaryLoop>();var edgesByLoop=new Dictionary<string,IReadOnlyList<Edge>>(StringComparer.Ordinal);int visits=0;
            for(int i=0;i<objects.Length;i++)
            {
                token.ThrowIfCancellationRequested();
                var loop=objects[i] as Loop2??throw new TopologyQueryException("Native loop object is unavailable.");
                if(loop.IsSingular())throw new TopologyQueryException("Singular face loops are unsupported.");
                var edgeObjects=TopologyArrayEvidence.RequireComplete(loop.GetEdges() as object[],loop.GetEdgeCount(),20000-visits,"loop edges");
                visits+=edgeObjects.Length;
                var edges=new List<Edge>();var keys=new List<string>();
                foreach(var edgeObject in edgeObjects)
                {
                    token.ThrowIfCancellationRequested();
                    var edge=edgeObject as Edge??throw new TopologyQueryException("Native loop edge is unavailable.");
                    edges.Add(edge);keys.Add(Identity(model,edge,"Edge"));
                }
                string loopKey="loop:"+i;
                facts.Add(new FaceBoundaryLoop{Key=loopKey,IsOuter=loop.IsOuter(),EdgeKeys=keys});edgesByLoop.Add(loopKey,edges);
            }
            var outer=FaceBoundaryLoops.Outer(facts);
            return new Boundary{Edges=edgesByLoop[outer.Key],Outer=outer,LoopCount=objects.Length};
        }
        private static string Identity(ModelDoc2 model,object entity,string kind)
        {
            var bytes=model.Extension.GetPersistReference3(entity) as byte[];
            if(bytes==null||bytes.Length==0||bytes.Length>1024*1024)throw new TopologyQueryException("Native boundary identity is unavailable.");
            using(var hash=SHA256.Create())return kind+":"+BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
    }
}
#endif
