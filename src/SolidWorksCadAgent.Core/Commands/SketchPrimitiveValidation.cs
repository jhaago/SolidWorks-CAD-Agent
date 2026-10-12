using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    // Shared by planning, simulation and native handlers so invalid geometry never reaches COM.
    public static class SketchPrimitiveValidation
    {
        public static string ValidateLine(JObject p) => Validate(p, false);
        public static string ValidateArc(JObject p) => Validate(p, true);
        private static string Validate(JObject p, bool arc)
        {
            var coordinates = arc
                ? new[] { "centerXmm", "centerYmm", "startXmm", "startYmm", "endXmm", "endYmm" }
                : new[] { "startXmm", "startYmm", "endXmm", "endYmm" };
            if (p == null) return "Sketch coordinates are required.";
            if (p.Properties().Any(property => !coordinates.Contains(property.Name) && !(arc && property.Name == "clockwise")))
                return "Unexpected sketch parameter.";
            foreach (var name in coordinates)
            {
                if (p[name]?.Type != JTokenType.Integer && p[name]?.Type != JTokenType.Float)
                    return name + " must be a number.";
                var value = p[name].Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) return name + " must be finite.";
            }
            var separation = Distance(p, "start", "end");
            if (double.IsInfinity(separation) || double.IsNaN(separation) || separation <= 0.000001)
                return "Start and end must be distinct by more than 0.000001 mm.";
            if (!arc) return null;
            if (p["clockwise"]?.Type != JTokenType.Boolean) return "clockwise must be an explicit boolean.";
            var radius = Distance(p, "center", "start");
            var endRadius = Distance(p, "center", "end");
            if (double.IsInfinity(radius) || double.IsInfinity(endRadius) || radius <= 0.000001 || endRadius <= 0.000001)
                return "Arc radius must be finite and greater than 0.000001 mm.";
            return Math.Abs(radius - endRadius) > Math.Max(0.000001, radius * 0.00000001)
                ? "Arc start and end must have the same radius about the center." : null;
        }
        private static double Distance(JObject p, string first, string second)
        {
            var x = (double)p[first + "Xmm"] - (double)p[second + "Xmm"];
            var y = (double)p[first + "Ymm"] - (double)p[second + "Ymm"];
            return Math.Sqrt(x * x + y * y);
        }
    }
}