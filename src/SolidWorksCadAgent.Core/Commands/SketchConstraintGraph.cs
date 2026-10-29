// Ported from czuryk/SolidworksMCP eccb8af, automation/parametric.py:
// RELATION_CODES and _validate_constraint_graph (MIT).
// Copyright (c) 2026 Samsaam Ali Baig. See third_party/SolidworksMCP/LICENSE.
// Changes: C#/net48, typed refs/arity, strict fields, dimensional units, bounded work.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    public sealed class SketchConstraintAnalysis
    {
        public bool Valid => Errors.Count == 0;
        public bool NativeSolved => false;
        public string Evidence => "Symbolic graph validation only; native relation attachment and solver status still required.";
        public string RequestedSolveTarget { get; internal set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> RelationCodes { get; } = new List<string>();
        public int EntityCount { get; internal set; }
        public int DimensionCount { get; internal set; }
    }

    public static class SketchConstraintGraph
    {
        private static readonly Regex Identifier = new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
        private static readonly IReadOnlyDictionary<string,string> Codes = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            {"coincident","sgCOINCIDENT"},{"horizontal","sgHORIZONTAL2D"},{"vertical","sgVERTICAL2D"},
            {"tangent","sgTANGENT"},{"concentric","sgCONCENTRIC"},{"equal","sgSAMELENGTH"},
            {"midpoint","sgATMIDDLE"},{"symmetric","sgSYMMETRIC"},{"parallel","sgPARALLEL"},
            {"perpendicular","sgPERPENDICULAR"},{"collinear","sgCOLINEAR"},{"fixed","sgFIXED"}
        };

        public static SketchConstraintAnalysis Analyze(JObject graph)
        {
            var result = new SketchConstraintAnalysis { RequestedSolveTarget = "fully_defined" };
            if(graph == null) { result.Errors.Add("A sketch graph object is required."); return result; }
            CheckKeys(graph,result,"entities","constraints","dimensions","solveTarget","allowFixed");
            if(graph["solveTarget"] != null)
            {
                var target = Text(graph,"solveTarget",result);
                if(target != "fully_defined" && target != "allow_under_defined") result.Errors.Add("Unsupported solveTarget.");
                else result.RequestedSolveTarget = target;
            }
            bool allowFixed = false;
            if(graph["allowFixed"] != null)
            {
                if(graph["allowFixed"].Type != JTokenType.Boolean) result.Errors.Add("allowFixed must be boolean.");
                else allowFixed = graph.Value<bool>("allowFixed");
            }
            var entities = Array(graph,"entities",256,result,true);
            var constraints = Array(graph,"constraints",1024,result);
            var dimensions = Array(graph,"dimensions",256,result);
            if(entities == null || constraints == null || dimensions == null) return result;
            result.EntityCount = entities.Count; result.DimensionCount = dimensions.Count;
            var types = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var item in entities)
            {
                var entity = item as JObject;
                if(entity == null) { result.Errors.Add("Every entity must be an object."); continue; }
                CheckKeys(entity,result,"id","type","construction");
                string id = Text(entity,"id",result), type = Text(entity,"type",result);
                if(!Name(id) || Reserved(id)) { result.Errors.Add("Entity id must be a unique non-reserved identifier."); continue; }
                if(!new[]{"line","arc","circle","point"}.Contains(type)) { result.Errors.Add("Unsupported entity type: "+type); continue; }
                if(types.ContainsKey(id)) result.Errors.Add("Duplicate entity id: "+id);
                else types.Add(id,type);
                if(entity["construction"] != null && entity["construction"].Type != JTokenType.Boolean) result.Errors.Add("construction must be boolean.");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in constraints)
            {
                var constraint = item as JObject;
                if(constraint == null) { result.Errors.Add("Every constraint must be an object."); continue; }
                int before = result.Errors.Count;
                CheckKeys(constraint,result,"type","entities","about");
                string type = Text(constraint,"type",result)?.ToLowerInvariant();
                if(type == null || !Codes.ContainsKey(type)) { result.Errors.Add("Unsupported constraint type."); continue; }
                if(type == "fixed" && !allowFixed) result.Errors.Add("Fix relations require explicit allowFixed trace opt-in.");
                var refs = References(constraint,types,result);
                if(constraint["about"] != null)
                {
                    string about = Text(constraint,"about",result);
                    if(type != "symmetric") result.Errors.Add("about applies only to symmetry.");
                    refs.Add(Resolve(about,types,result));
                }
                string canonical = type+":"+string.Join("|",refs.Select(r=>r.Key).OrderBy(s=>s,StringComparer.Ordinal));
                if(!seen.Add(canonical)) result.Errors.Add("Duplicate relation: "+type);
                if(refs.Select(r=>r.Key).Distinct(StringComparer.Ordinal).Count() != refs.Count) result.Errors.Add("A relation cannot select the same reference twice.");
                if(!Arity(type,refs)) result.Errors.Add("Constraint entity types or arity are invalid: "+type);
                if(result.Errors.Count == before) result.RelationCodes.Add(Codes[type]);
            }
            var dimensionNames = new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in dimensions)
            {
                var dimension = item as JObject;
                if(dimension == null) { result.Errors.Add("Every dimension must be an object."); continue; }
                CheckKeys(dimension,result,"id","type","entities","value","unit");
                string id = Text(dimension,"id",result), type = Text(dimension,"type",result), unit = Text(dimension,"unit",result);
                if(!Name(id) || !dimensionNames.Add(id)) result.Errors.Add("Dimension id is invalid or duplicated.");
                var refs = References(dimension,types,result);
                bool valid = type == "length" ? refs.Count == 1 && Line(refs[0]) :
                    type == "diameter" ? refs.Count == 1 && refs[0].Type == "circle" :
                    type == "radius" ? refs.Count == 1 && Curved(refs[0]) :
                    type == "angle" ? refs.Count == 2 && refs.All(Line) :
                    (type == "horizontal" || type == "vertical") && refs.Count == 2 && refs.All(Point);
                if(!valid || refs.Select(r=>r.Key).Distinct().Count() != refs.Count) result.Errors.Add("Dimension targets or arity are invalid.");
                if(unit != (type == "angle" ? "deg" : "mm")) result.Errors.Add("Dimension unit differs from its type.");
                var value = dimension["value"];
                if(value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) result.Errors.Add("Dimension value must be numeric.");
                else
                {
                    double number = value.Value<double>();
                    double native = type == "angle" ? number * (Math.PI / 180) : number / 1000;
                    if(double.IsNaN(number) || double.IsInfinity(number) || number <= 0 || native <= 0 || double.IsInfinity(native) || (type == "angle" && number >= 180)) result.Errors.Add("Dimension value must be finite, positive and native-representable.");
                }
            }
            return result;
        }

        private sealed class Ref { public string Key,Type; }
        private static bool Point(Ref r) => r.Type == "point";
        private static bool Line(Ref r) => r.Type == "line";
        private static bool Curved(Ref r) => r.Type == "arc" || r.Type == "circle";
        private static bool Arity(string type,List<Ref> refs)
        {
            switch(type)
            {
                case "fixed": return refs.Count == 1;
                case "horizontal": case "vertical": return refs.Count == 1 && Line(refs[0]) || refs.Count == 2 && refs.All(Point);
                case "coincident": return refs.Count == 2 && refs.All(Point);
                case "parallel": case "perpendicular": case "collinear": return refs.Count == 2 && refs.All(Line);
                case "concentric": return refs.Count == 2 && refs.All(Curved);
                case "equal": return refs.Count == 2 && (refs.All(Line) || refs.All(Curved));
                case "tangent": return refs.Count == 2 && refs.All(r=>Line(r)||Curved(r)) && refs.Any(Curved);
                case "midpoint": return refs.Count == 2 && refs.Count(Point) == 1 && refs.Count(Line) == 1;
                case "symmetric": return refs.Count == 3 && Point(refs[0]) && Point(refs[1]) && Line(refs[2]);
                default: return false;
            }
        }
        private static List<Ref> References(JObject owner,Dictionary<string,string> entities,SketchConstraintAnalysis result)
        {
            var list = Array(owner,"entities",3,result,true);
            return list == null ? new List<Ref>() : list.Select(value=>Resolve(value.Type == JTokenType.String ? (string)value : null,entities,result)).ToList();
        }
        private static Ref Resolve(string name,Dictionary<string,string> entities,SketchConstraintAnalysis result)
        {
            if(name == "origin" || name == "sketch_origin") return new Ref { Key="origin",Type="point" };
            var parts = (name??"").Split('.');
            if(parts.Length <= 2 && entities.TryGetValue(parts[0],out var type))
            {
                if(parts.Length == 1) return new Ref { Key=name,Type=type };
                if((parts[1] == "start" || parts[1] == "end") && (type == "line" || type == "arc") || parts[1] == "center" && (type == "circle" || type == "arc"))
                    return new Ref { Key=name,Type="point" };
            }
            result.Errors.Add("Unknown or incompatible entity reference: "+name);
            return new Ref { Key=name??"invalid",Type="invalid" };
        }
        private static bool Name(string name) => name != null && Identifier.IsMatch(name);
        private static bool Reserved(string id) => new[]{"origin","sketch_origin","axis_x","axis_y"}.Contains(id);
        private static JArray Array(JObject value,string name,int maximum,SketchConstraintAnalysis result,bool required=false)
        {
            if(value[name] == null && !required) return new JArray();
            var array = value[name] as JArray;
            if(array == null || array.Count > maximum || required && array.Count == 0)
            { result.Errors.Add(name+" must be a bounded"+(required?" nonempty":"")+" array (maximum "+maximum+")."); return null; }
            return array;
        }
        private static string Text(JObject value,string name,SketchConstraintAnalysis result)
        {
            if(value[name]?.Type == JTokenType.String && ((string)value[name]).Length <= 128 && !string.IsNullOrWhiteSpace((string)value[name])) return (string)value[name];
            result.Errors.Add(name+" must be a bounded nonempty string."); return null;
        }
        private static void CheckKeys(JObject obj,SketchConstraintAnalysis result,params string[] allowed)
        { if(obj.Properties().Any(p=>!allowed.Contains(p.Name))) result.Errors.Add("Unknown sketch graph property."); }
    }
}
