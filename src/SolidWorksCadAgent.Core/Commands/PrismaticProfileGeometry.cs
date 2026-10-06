using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    // Produces deterministic native sketch primitives; all coordinates remain in millimetres.
    public static class PrismaticProfileGeometry
    {
        public static string ValidateSlot(JObject parameters)
        {
            var error = ValidateNumbers(parameters, "centerXmm", "centerYmm", "lengthMm", "widthMm", "angleDegrees");
            if (error != null) return error;
            double length = Number(parameters, "lengthMm"), width = Number(parameters, "widthMm");
            if (width <= 0 || length <= width) return "Slot total length must exceed its positive width.";
            if (Math.Abs(Number(parameters, "angleDegrees")) > 360) return "angleDegrees must be between -360 and 360.";
            return ValidatePrimitives(BuildSlot(parameters));
        }

        public static string ValidateRegularPolygon(JObject parameters)
        {
            var error = ValidateNumbers(parameters, "centerXmm", "centerYmm", "sides", "diameterMm", "angleDegrees");
            if (error != null) return error;
            if (parameters["sides"].Type != JTokenType.Integer || Number(parameters, "sides") < 3 || Number(parameters, "sides") > 32)
                return "sides must be an integer between 3 and 32.";
            if (Number(parameters, "diameterMm") <= 0) return "diameterMm must be positive.";
            if (Math.Abs(Number(parameters, "angleDegrees")) > 360) return "angleDegrees must be between -360 and 360.";
            return ValidatePrimitives(BuildPolygon(parameters));
        }

        public static string ValidateCut(JObject parameters)
        {
            if (parameters == null || parameters["endCondition"]?.Type != JTokenType.String)
                return "CutExtrude requires an explicit endCondition.";
            string condition = (string)parameters["endCondition"];
            if (condition == "ThroughAll")
                return parameters.Properties().Any(p => p.Name != "endCondition")
                    ? "ThroughAll does not accept depthMm or other parameters." : null;
            if (condition != "Blind") return "CutExtrude endCondition must be Blind or ThroughAll.";
            if (parameters.Properties().Any(p => p.Name != "endCondition" && p.Name != "depthMm"))
                return "Unexpected CutExtrude parameter.";
            var error = ValidateNumber(parameters, "depthMm");
            if (error != null) return error;
            double depthMetres = Number(parameters, "depthMm") / 1000.0;
            return depthMetres <= 0 || double.IsNaN(depthMetres) || double.IsInfinity(depthMetres)
                ? "Blind depthMm must remain a finite positive depth after conversion to metres." : null;
        }

        public static IReadOnlyList<CadCommandEnvelope> Slot(JObject parameters)
        {
            var error = ValidateSlot(parameters);
            if (error != null) throw new ArgumentException(error, nameof(parameters));
            return BuildSlot(parameters);
        }

        public static IReadOnlyList<CadCommandEnvelope> RegularPolygon(JObject parameters)
        {
            var error = ValidateRegularPolygon(parameters);
            if (error != null) throw new ArgumentException(error, nameof(parameters));
            return BuildPolygon(parameters);
        }

        private static IReadOnlyList<CadCommandEnvelope> BuildSlot(JObject p)
        {
            double halfStraight = (Number(p, "lengthMm") - Number(p, "widthMm")) / 2;
            double radius = Number(p, "widthMm") / 2;
            var a = Transform(p, -halfStraight, -radius);
            var b = Transform(p, halfStraight, -radius);
            var c = Transform(p, halfStraight, radius);
            var d = Transform(p, -halfStraight, radius);
            return new[] { Line(a, b), Arc(Transform(p, halfStraight, 0), b, c), Line(c, d), Arc(Transform(p, -halfStraight, 0), d, a) };
        }

        private static IReadOnlyList<CadCommandEnvelope> BuildPolygon(JObject p)
        {
            int sides = (int)p["sides"];
            double radius = Number(p, "diameterMm") / 2;
            var points = Enumerable.Range(0, sides).Select(i =>
                Transform(p, radius * Math.Cos(2 * Math.PI * i / sides), radius * Math.Sin(2 * Math.PI * i / sides))).ToArray();
            return Enumerable.Range(0, sides).Select(i => Line(points[i], points[(i + 1) % sides])).ToArray();
        }

        private static Tuple<double, double> Transform(JObject p, double x, double y)
        {
            double angle = Number(p, "angleDegrees") * Math.PI / 180;
            return Tuple.Create(Number(p, "centerXmm") + x * Math.Cos(angle) - y * Math.Sin(angle),
                Number(p, "centerYmm") + x * Math.Sin(angle) + y * Math.Cos(angle));
        }

        private static CadCommandEnvelope Line(Tuple<double, double> start, Tuple<double, double> end) =>
            new CadCommandEnvelope { Command = CadCommandNames.AddLine, Parameters = JObject.FromObject(new
            {
                startXmm = start.Item1, startYmm = start.Item2, endXmm = end.Item1, endYmm = end.Item2
            }) };

        private static CadCommandEnvelope Arc(Tuple<double, double> center, Tuple<double, double> start, Tuple<double, double> end)
        {
            var command = Line(start, end);
            command.Command = CadCommandNames.AddArc;
            command.Parameters["centerXmm"] = center.Item1;
            command.Parameters["centerYmm"] = center.Item2;
            command.Parameters["clockwise"] = false;
            return command;
        }

        private static string ValidatePrimitives(IReadOnlyList<CadCommandEnvelope> commands)
        {
            foreach (var command in commands)
            {
                var error = command.Command == CadCommandNames.AddLine
                    ? SketchPrimitiveValidation.ValidateLine(command.Parameters)
                    : SketchPrimitiveValidation.ValidateArc(command.Parameters);
                if (error != null) return "Profile is numerically degenerate: " + error;
            }
            return null;
        }

        private static string ValidateNumbers(JObject p, params string[] names)
        {
            if (p == null) return "Profile parameters are required.";
            if (p.Properties().Any(property => !names.Contains(property.Name))) return "Unexpected profile parameter.";
            foreach (var name in names)
            {
                var error = ValidateNumber(p, name);
                if (error != null) return error;
            }
            return null;
        }

        private static string ValidateNumber(JObject p, string name)
        {
            if (p[name]?.Type != JTokenType.Integer && p[name]?.Type != JTokenType.Float) return name + " must be a number.";
            try
            {
                double value = Number(p, name);
                return double.IsNaN(value) || double.IsInfinity(value) ? name + " must be finite." : null;
            }
            catch (Exception e) when (e is OverflowException || e is FormatException || e is InvalidCastException)
            {
                return name + " must be a finite representable number.";
            }
        }

        private static double Number(JObject p, string name) => p[name].Value<double>();
    }
}

