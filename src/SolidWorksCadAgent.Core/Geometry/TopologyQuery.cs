// Adapted from soheil-mp/solidworks-mcp f450dab, Geometry/TopoQuery.cs and Model/EntityRef.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: COM-free facts, net48/Newtonsoft, strict bounded contracts.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    public sealed class TopologyQuery
    {
        public string Kind { get; private set; }
        public int? ExpectedCount { get; private set; }
        public bool OrderAsChain { get; private set; }
        private string _surface,_curve,_feature,_body;
        private Vec3? _normal,_parallel,_extreme;
        private double? _radius,_length;
        private double _radiusTolerance=0.001,_lengthTolerance=0.001,_angleRadians=Math.PI/180;
        private TopologyQuery() { }
        public static TopologyQuery Parse(JObject input)
        {
            if(input==null)throw new ArgumentException("Topology query is required.");
            Keys(input,"kind","where","expect","orderChain");
            var query=new TopologyQuery{Kind=Text(input,"kind",true)};
            if(query.Kind!="Face"&&query.Kind!="Edge")throw new ArgumentException("kind must be Face or Edge.");
            if(input["orderChain"]!=null)
            {
                if(input["orderChain"].Type!=JTokenType.Boolean||query.Kind!="Edge")throw new ArgumentException("orderChain is a boolean edge-only option.");
                query.OrderAsChain=input.Value<bool>("orderChain");
            }
            if(input["expect"]!=null)
            {
                if(input["expect"].Type!=JTokenType.Integer)throw new ArgumentException("expect must be an integer.");
                if(!int.TryParse(input["expect"].ToString(),System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,out var count)||count<0||count>256)
                    throw new ArgumentException("expect must be between 0 and 256.");
                query.ExpectedCount=count;
            }
            if(input["where"]==null)return query;
            var where=input["where"] as JObject??throw new ArgumentException("where must be an object.");
            Keys(where,"surface","curve","normal","parallelTo","radiusMm","radiusTolMm","lengthMm","lengthTolMm","extreme","feature","body","angleTolDeg");
            query._surface=Text(where,"surface");query._curve=Text(where,"curve");query._feature=Text(where,"feature");query._body=Text(where,"body");
            if(query._surface!=null&&query.Kind!="Face"||query._curve!=null&&query.Kind!="Edge"||where["normal"]!=null&&query.Kind!="Face"||where["lengthMm"]!=null&&query.Kind!="Edge")
                throw new ArgumentException("Filter does not apply to this topology kind.");
            if(query._surface!=null&&!new[]{"plane","cylinder","cone","sphere","torus","bspline","other"}.Contains(query._surface))throw new ArgumentException("Unsupported surface type.");
            if(query._curve!=null&&!new[]{"line","circle","ellipse","bspline","other"}.Contains(query._curve))throw new ArgumentException("Unsupported curve type.");
            query._normal=Axis(where,"normal");query._parallel=Axis(where,"parallelTo");query._extreme=Axis(where,"extreme");
            query._radius=Number(where,"radiusMm");query._length=Number(where,"lengthMm");
            query._radiusTolerance=Number(where,"radiusTolMm")??0.001;query._lengthTolerance=Number(where,"lengthTolMm")??0.001;
            double angle=Number(where,"angleTolDeg")??1;
            if(angle>=90)throw new ArgumentException("angleTolDeg must be positive and below 90.");
            query._angleRadians=angle*Math.PI/180;
            return query;
        }
        public IReadOnlyList<TopologyFact> Apply(IReadOnlyList<TopologyFact> source)
        {
            if(source==null||source.Count>20000)throw new TopologyQueryException("Native topology is unavailable or exceeds the inspection budget.");
            var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in source)
            {
                if(item==null||string.IsNullOrWhiteSpace(item.InspectionId)||!identities.Add(item.InspectionId)||
                    (item.Kind!="Face"&&item.Kind!="Edge")||string.IsNullOrWhiteSpace(item.Geometry))
                    throw new TopologyQueryException("Invalid or duplicate topology identity.");
                foreach(var measurement in new[]{item.LengthMm,item.RadiusMm,item.AreaMm2})
                    if(measurement.HasValue&&(!Vec3.Finite(measurement.Value)||measurement.Value<=0))throw new TopologyQueryException("Native topology measurement is invalid.");
                if(item.Normal.HasValue)item.Normal.Value.Normalized();
                if(item.Direction.HasValue)item.Direction.Value.Normalized();
                if(item.Kind==Kind&&_feature!=null&&string.IsNullOrWhiteSpace(item.FeatureName)&&
                    (item.FeatureNames==null||item.FeatureNames.Length==0||item.FeatureNames.Any(string.IsNullOrWhiteSpace)))
                    throw new TopologyQueryException("Feature ownership evidence is unavailable; a feature filter cannot be evaluated.");
            }
            var found=source.Where(Matches).ToList();
            if(_extreme.HasValue&&found.Count>0)
            {
                double best=found.Max(i=>i.PositionMm.Dot(_extreme.Value));
                if(!Vec3.Finite(best))throw new TopologyQueryException("Extreme projection is not finite.");
                found=found.Where(i=>best-i.PositionMm.Dot(_extreme.Value)<=0.001).ToList();
            }
            if(ExpectedCount.HasValue&&found.Count!=ExpectedCount.Value)
                throw new TopologyQueryException("Expected "+ExpectedCount.Value+" topology matches; found "+found.Count+". Refine the query; no first-match fallback is permitted.");
            if(found.Count>256)throw new TopologyQueryException("Topology query exceeds the 256-result budget; refine the filters.");
            return found.OrderBy(i=>i.BodyName,StringComparer.Ordinal).ThenBy(i=>Math.Round(i.PositionMm.X,6))
                .ThenBy(i=>Math.Round(i.PositionMm.Y,6)).ThenBy(i=>Math.Round(i.PositionMm.Z,6)).ThenBy(i=>i.InspectionId,StringComparer.Ordinal).ToArray();
        }
        public static ChainPlan OrderStraightEdges(IReadOnlyList<TopologyFact> matches,double toleranceMm=0.001)
        {
            if(matches==null||matches.Any(f=>f==null||f.Kind!="Edge"||f.Geometry!="line"||!f.StartMm.HasValue||!f.EndMm.HasValue))
                throw new TopologyQueryException("Chain ordering requires measured straight-edge endpoints.");
            var plan=EdgeChain.Order(matches.Select(f=>(f.StartMm.Value,f.EndMm.Value)).ToArray(),toleranceMm);
            return plan??throw new TopologyQueryException("Edges are disconnected, duplicate, degenerate or branched.");
        }
        private bool Matches(TopologyFact item)
        {
            if(item.Kind!=Kind||_surface!=null&&!Eq(item.Geometry,_surface)||_curve!=null&&!Eq(item.Geometry,_curve)||
                _feature!=null&&!Eq(item.FeatureName,_feature)&&!(item.FeatureNames?.Any(name=>Eq(name,_feature))??false)||_body!=null&&!Eq(item.BodyName,_body))return false;
            if(_radius.HasValue&&(!item.RadiusMm.HasValue||Math.Abs(item.RadiusMm.Value-_radius.Value)>_radiusTolerance))return false;
            if(_length.HasValue&&(!item.LengthMm.HasValue||Math.Abs(item.LengthMm.Value-_length.Value)>_lengthTolerance))return false;
            if(_normal.HasValue&&(!item.Normal.HasValue||item.Normal.Value.Normalized().Dot(_normal.Value)<Math.Cos(_angleRadians)))return false;
            if(_parallel.HasValue&&(!item.Direction.HasValue||!item.Direction.Value.IsParallelTo(_parallel.Value,_angleRadians)))return false;
            return true;
        }
        private static bool Eq(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        private static Vec3? Axis(JObject value,string name)=>value[name]==null?(Vec3?)null:AxisSpec.Parse(Text(value,name,true));
        private static string Text(JObject value,string name,bool required=false)
        {
            if(value[name]==null&&!required)return null;
            if(value[name]?.Type!=JTokenType.String||string.IsNullOrWhiteSpace((string)value[name])||((string)value[name]).Length>256)throw new ArgumentException(name+" must be a nonempty bounded string.");
            return (string)value[name];
        }
        private static double? Number(JObject value,string name)
        {
            var token=value[name];if(token==null)return null;
            if(token.Type!=JTokenType.Float&&token.Type!=JTokenType.Integer)throw new ArgumentException(name+" must be numeric.");
            var n=token.Value<double>();if(!Vec3.Finite(n)||n<=0)throw new ArgumentException(name+" must be finite and positive.");return n;
        }
        private static void Keys(JObject value,params string[] allowed)
        { if(value.Properties().Any(p=>!allowed.Contains(p.Name)))throw new ArgumentException("Unknown topology query property."); }
    }
}
