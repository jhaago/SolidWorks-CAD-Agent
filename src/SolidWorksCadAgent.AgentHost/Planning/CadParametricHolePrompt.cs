using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    public static class CadParametricHolePrompt
    {
        private const string Prefix="V2 parametric plate with hole:";
        private static readonly Regex Syntax=new Regex(@"\AV2 parametric plate with hole:\s*(?<plate>\d+(?:\.\d+)?\s*x\s*\d+(?:\.\d+)?\s*x\s*\d+(?:\.\d+)?\s*mm)\s*;\s*hole diameter\s+(?<diameter>\d+(?:\.\d+)?)\s*mm\s+at\s+x\s*=\s*(?<x>\d+(?:\.\d+)?)\s*mm\s*,\s*y\s*=\s*(?<y>\d+(?:\.\d+)?)\s*mm\s*;\s*save as\s+(?<path>[^\r\n]+)\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        public static bool IsRequest(string prompt)=>prompt?.TrimStart().StartsWith(Prefix,StringComparison.OrdinalIgnoreCase)==true;
        public static string BuildCandidate(string prompt)
        {
            var m=Syntax.Match(prompt?.Trim()??"");if(!m.Success)throw new ArgumentException("Use: V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as folder/hole.sldprt");
            var plan=JObject.Parse(CadParametricPlatePrompt.BuildCandidate("V2 parametric plate: "+m.Groups["plate"].Value+"; save as "+m.Groups["path"].Value));
            var diameter=Number(m,"diameter");var x=Number(m,"x");var y=Number(m,"y");
            var steps=(JArray)plan["steps"];var save=steps[5];save.Remove();
            steps.Add(new JObject{["stepKey"]="holeSketch",["command"]="CreateSketch",["operationVersion"]=1,["parameters"]=new JObject{["plane"]="Top Plane"},["outputKey"]="holeProfile"});
            steps.Add(Step("holeCircle","AddCircle",2,new JObject{["centerXmm"]=x,["centerYmm"]=y,["diameterMm"]=diameter}));
            steps.Add(Step("holeExit","ExitSketch",1,new JObject()));
            var cut=Step("holeCut","CutExtrude",2,new JObject{["endCondition"]="ThroughAll"});
            cut["inputs"]=new JObject{["profileSketch"]=new JObject{["kind"]="Sketch",["outputKey"]="holeProfile"}};steps.Add(cut);steps.Add(save);
            plan["summary"]=plan.Value<string>("summary")+string.Format(CultureInfo.InvariantCulture," Add one through-all hole with driving diameter {0} mm, X {1} mm and Y {2} mm from the Top Plane origin.",diameter,x,y);
            var candidate=plan.ToString();var parsed=CadPlanDocumentReader.ReadCandidate(candidate);
            if(!parsed.IsValid)throw new ArgumentException(string.Join(" ",parsed.Errors));return candidate;
        }
        private static double Number(Match m,string group)
        {
            if(!double.TryParse(m.Groups[group].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||double.IsNaN(value)||double.IsInfinity(value))
                throw new ArgumentException("Hole dimensions must be finite millimetre values.");return value;
        }
        private static JObject Step(string key,string command,int version,JObject parameters)=>new JObject{["stepKey"]=key,["command"]=command,["operationVersion"]=version,["parameters"]=parameters};
    }
}

