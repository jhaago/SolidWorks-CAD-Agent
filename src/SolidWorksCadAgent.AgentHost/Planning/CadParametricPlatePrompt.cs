using System;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json.Linq;
namespace SolidWorksCadAgent.AgentHost.Planning
{
    public static class CadParametricPlatePrompt
    {
        private const string Prefix="V2 parametric plate:";
        public static bool IsRequest(string prompt)=>prompt?.TrimStart().StartsWith(Prefix,StringComparison.OrdinalIgnoreCase)==true;
        public static string BuildCandidate(string prompt)
        {
            if(!IsRequest(prompt))throw new ArgumentException("Use: V2 parametric plate: 120 x 60 x 10 mm; save as folder/plate.sldprt");
            var plan=JObject.Parse(CadV2RectangleBossPrompt.BuildCandidate("V2 rectangle boss:"+prompt.Trim().Substring(Prefix.Length)));
            var steps=(JArray)plan["steps"];var path=steps[5]["parameters"].Value<string>("path");
            if(!path.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||path.Split(new[]{'/','\\'}).Any(p=>p==".."||p=="."))
                throw new ArgumentException("Specify a new relative .sldprt output path without traversal.");
            steps[2]["operationVersion"]=2;steps[4]["operationVersion"]=3;
            var width=steps[2]["parameters"].Value<double>("widthMm");var length=steps[2]["parameters"].Value<double>("heightMm");var thickness=steps[4]["parameters"].Value<double>("depthMm");
            plan["summary"]=string.Format(CultureInfo.InvariantCulture,"Create a fully defined Top Plane plate with driving width {0} mm, length {1} mm and thickness {2} mm; save after native parameter verification.",width,length,thickness);
            return plan.ToString();
        }
    }
}
