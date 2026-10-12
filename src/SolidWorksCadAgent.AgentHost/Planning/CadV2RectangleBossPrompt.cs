using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>Explicit narrow v2 syntax accepted by the existing desktop and paired-phone CAD job input.</summary>
    public static class CadV2RectangleBossPrompt
    {
        private static readonly Regex Syntax = new Regex(
            @"\AV2 rectangle boss:\s*(?<width>\d+(?:\.\d+)?)\s*x\s*(?<height>\d+(?:\.\d+)?)\s*x\s*(?<depth>\d+(?:\.\d+)?)\s*mm\s*;\s*save as\s+(?<path>[^\r\n]+)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsRequest(string prompt) =>
            prompt != null && prompt.TrimStart().StartsWith("V2 rectangle boss:", StringComparison.OrdinalIgnoreCase);

        public static string BuildCandidate(string prompt)
        {
            var match = Syntax.Match(prompt?.Trim() ?? string.Empty);
            if (!match.Success)
                throw new ArgumentException("Use: V2 rectangle boss: 20 x 10 x 5 mm; save as folder/part.sldprt");
            var width = Positive(match.Groups["width"].Value);
            var height = Positive(match.Groups["height"].Value);
            var depth = Positive(match.Groups["depth"].Value);
            var path = match.Groups["path"].Value.Trim();
            if (path.Length == 0 || path.Length > 260 || Path.IsPathRooted(path) ||
                path.Contains("\"") || path.Contains(";") || path.Contains(":"))
                throw new ArgumentException("Specify one relative .sldprt output path without quotes or extra instructions.");
            return new JObject
            {
                ["planVersion"] = 2,
                ["summary"] = "Create a new Top Plane centred rectangle boss and save a new part after verification.",
                ["steps"] = new JArray
                {
                    Step("new", "NewPart", 1, new JObject()),
                    new JObject { ["stepKey"] = "sketch", ["command"] = "CreateSketch", ["operationVersion"] = 1,
                        ["parameters"] = new JObject { ["plane"] = "Top Plane" }, ["outputKey"] = "profile" },
                    Step("rectangle", "AddRectangle", 1, new JObject { ["centerXmm"] = 0.0, ["centerYmm"] = 0.0,
                        ["widthMm"] = width, ["heightMm"] = height }),
                    Step("exit", "ExitSketch", 1, new JObject()),
                    new JObject { ["stepKey"] = "boss", ["command"] = "Extrude", ["operationVersion"] = 2,
                        ["parameters"] = new JObject { ["depthMm"] = depth },
                        ["inputs"] = new JObject { ["profileSketch"] = new JObject { ["kind"] = "Sketch", ["outputKey"] = "profile" } } },
                    Step("save", "SavePart", 1, new JObject { ["path"] = path, ["allowOverwrite"] = false })
                }
            }.ToString(Formatting.None);
        }

        private static JObject Step(string key, string command, int version, JObject parameters) =>
            new JObject { ["stepKey"] = key, ["command"] = command, ["operationVersion"] = version,
                ["parameters"] = parameters };

        private static double Positive(string text)
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 10000)
                throw new ArgumentException("Width, height and depth must each be positive millimetre values no greater than 10000.");
            return value;
        }
    }
}
