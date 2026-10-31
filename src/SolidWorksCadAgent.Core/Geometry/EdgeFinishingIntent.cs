// Adapted in part from soheil-mp/solidworks-mcp f450dab, Services/FeatureService.cs and PartFeatureService.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors. See third_party/solidworks-mcp/LICENSE.
// Changes: bounded net48 pure checks; strict evidence, orthogonal equal-leg chamfers, no unmeasured fallback.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace SolidWorksCadAgent.Core.Geometry
{
    [JsonConverter(typeof(StringEnumConverter),typeof(CamelCaseNamingStrategy))]
    public enum EdgeFinishingKind { Fillet,Chamfer }
    public sealed class EdgeFinishingDefinition
    {
        public EdgeFinishingKind Kind {get;set;}
        public string Variant {get;set;}
        public double SizeMm {get;set;}
        public double? AngleDegrees {get;set;}
        public int EdgeCount {get;set;}
        public bool TangentPropagation {get;set;}
        public bool MultipleRadius {get;set;}
        public bool Asymmetric {get;set;}
    }
    public sealed class FinishingLine
    {
        public string Key {get;set;}
        public Vec3 PointMm {get;set;}
        public Vec3 Direction {get;set;}
    }
    public static class EdgeFinishingIntent
    {
        public static void VerifyDefinition(EdgeFinishingKind kind,double sizeMm,int edgeCount,EdgeFinishingDefinition actual)
        {
            Request(kind,sizeMm);if(edgeCount<1||edgeCount>256)throw new ArgumentOutOfRangeException(nameof(edgeCount));
            if(actual==null||actual.Kind!=kind||actual.Variant!=(kind==EdgeFinishingKind.Fillet?"constant_radius":"angle_distance")||
                !Vec3.Finite(actual.SizeMm)||actual.SizeMm<=0||Math.Abs(actual.SizeMm-sizeMm)>Tolerance(sizeMm)||
                actual.EdgeCount!=edgeCount||actual.TangentPropagation||actual.MultipleRadius||actual.Asymmetric)
                throw new InvalidOperationException("Native finishing definition differs from the requested kind, size, edge count or bounded options.");
            if(kind==EdgeFinishingKind.Chamfer&&(!actual.AngleDegrees.HasValue||!Vec3.Finite(actual.AngleDegrees.Value)||Math.Abs(actual.AngleDegrees.Value-45)>1e-6))
                throw new InvalidOperationException("Native chamfer angle is not the requested 45 degrees.");
        }
        public static void VerifyRoundRadii(double sizeMm,IReadOnlyList<double> radiiMm)
        {
            Request(EdgeFinishingKind.Fillet,sizeMm);
            if(radiiMm==null||radiiMm.Count==0||radiiMm.Count>20000||radiiMm.Any(r=>!Vec3.Finite(r)||r<=0||Math.Abs(r-sizeMm)>Tolerance(sizeMm)))
                throw new InvalidOperationException("Native fillet round-face radii are unavailable or differ from the requested radius; no unmeasured fallback is accepted.");
        }
        public static void VerifyChamferLegs(double distanceMm,IReadOnlyList<FinishingLine> original,IReadOnlyList<IReadOnlyList<FinishingLine>> faces)
        {
            Request(EdgeFinishingKind.Chamfer,distanceMm);
            if(original==null||original.Count==0||original.Count>256||faces==null||faces.Count==0||faces.Count>20000)
                throw new InvalidOperationException("Bounded original edge and planar chamfer-face evidence is required.");
            var ids=new HashSet<string>(StringComparer.Ordinal);int visits=0;
            foreach(var line in original){ValidateLine(line);if(!ids.Add(line.Key))throw new InvalidOperationException("Original finishing edges are duplicated.");}
            foreach(var face in faces)
            {
                if(face==null||face.Count==0||face.Count>20000-visits)throw new InvalidOperationException("Chamfer-face line evidence is incomplete or exceeds its budget.");
                visits+=face.Count;foreach(var line in face)ValidateLine(line);
            }
            foreach(var edge in original)
            {
                bool found=false;var u=edge.Direction.Normalized();
                foreach(var face in faces)
                {
                    var offsets=new List<Vec3>();
                    foreach(var line in face)
                    {
                        if(!line.Direction.IsParallelTo(u,1e-6))continue;
                        var delta=line.PointMm-edge.PointMm;var radial=delta-u*delta.Dot(u);
                        if(!offsets.Any(v=>v.DistanceTo(radial)<=Tolerance(distanceMm)))offsets.Add(radial);
                    }
                    if(offsets.Count!=2||offsets.Any(v=>!Vec3.Finite(v.Length)||Math.Abs(v.Length-distanceMm)>Tolerance(distanceMm)))continue;
                    if(Math.Abs(offsets[0].Normalized().Dot(offsets[1].Normalized()))>1e-6)continue;
                    found=true;break;
                }
                if(!found)throw new InvalidOperationException("No native planar chamfer face has two requested orthogonal legs along "+edge.Key+".");
            }
        }
        public static void VerifyInventory(IReadOnlyDictionary<string,string> before,IReadOnlyDictionary<string,string> after,string name,string nativeType)
        {
            var diff=FeatureInventory.Compare(before,after);
            if(diff.Added.Length!=1||diff.Added[0]!=name||diff.Removed.Length!=0||diff.TypeChanged.Length!=0||!after.TryGetValue(name,out var type)||type!=nativeType)
                throw new InvalidOperationException("Finishing must add exactly its named native feature and preserve the existing feature inventory.");
        }
        public static string NextFeatureName(EdgeFinishingKind kind,IReadOnlyDictionary<string,string> inventory)
        {
            Request(kind,1);if(inventory==null||inventory.Count>=10000)throw new ArgumentException("Feature inventory is unavailable or at its budget.");
            var names=new HashSet<string>(inventory.Keys,StringComparer.OrdinalIgnoreCase);
            string prefix=kind==EdgeFinishingKind.Fillet?"AgentFillet":"AgentChamfer";
            for(int i=1;i<=10000;i++)if(!names.Contains(prefix+i))return prefix+i;
            throw new InvalidOperationException("No unique finishing feature name is available.");
        }
        private static double Tolerance(double sizeMm)=>Math.Max(1e-5,sizeMm*1e-6);
        private static void Request(EdgeFinishingKind kind,double sizeMm)
        {
            if(kind!=EdgeFinishingKind.Fillet&&kind!=EdgeFinishingKind.Chamfer)throw new ArgumentOutOfRangeException(nameof(kind));
            if(!Vec3.Finite(sizeMm)||sizeMm<=0||sizeMm/1000<=0)throw new ArgumentOutOfRangeException(nameof(sizeMm));
        }
        private static void ValidateLine(FinishingLine line)
        {
            if(line==null||string.IsNullOrWhiteSpace(line.Key)||line.Key.Length>256||line.Direction.Equals(Vec3.Zero))
                throw new InvalidOperationException("Native line evidence or its direction is unavailable.");
        }
    }
}
