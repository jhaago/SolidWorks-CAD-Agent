using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public static class CadPlanningCommandContract
    {
        public static IReadOnlyList<string> AllowedCommands => CadOperationCatalog.Names;

        public static string ProtocolDescription => CadOperationCatalog.ProtocolDescription;

        public static string Validate(CadCommandEnvelope command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.Command))
                return "A CAD command name is required.";
            if (CadOperationCatalog.Find(command.Command) == null)
                return "The proposed CAD plan contains an unsupported command: " + command.Command;

            var p = command.Parameters ?? new JObject();
            switch (command.Command)
            {
                case CadCommandNames.NewPart:
                case CadCommandNames.CloseDocument:
                case CadCommandNames.ExitSketch:
                case CadCommandNames.Rebuild:
                    return Only(p);
                case CadCommandNames.OpenPart:
                    return First(Only(p, "path"), RequiredString(p, "path"));
                case CadCommandNames.SavePart:
                    return First(
                        Only(p, "path", "allowOverwrite"),
                        RequiredString(p, "path"),
                        OptionalBoolean(p, "allowOverwrite"));
                case CadCommandNames.CreateSketch:
                    var planeError = First(Only(p, "plane"), RequiredString(p, "plane"));
                    if (planeError != null) return planeError;
                    var plane = (string)p["plane"];
                    return plane == "Top Plane" || plane == "Front Plane" || plane == "Right Plane"
                        ? null
                        : "CreateSketch plane must be Top Plane, Front Plane, or Right Plane.";
                case CadCommandNames.AddLine:
                    return SketchPrimitiveValidation.ValidateLine(p);
                case CadCommandNames.AddArc:
                    return SketchPrimitiveValidation.ValidateArc(p);
                case CadCommandNames.AddRectangle:
                    return First(
                        Only(p, "centerXmm", "centerYmm", "widthMm", "heightMm"),
                        FiniteNumber(p, "centerXmm", false),
                        FiniteNumber(p, "centerYmm", false),
                        FiniteNumber(p, "widthMm", true),
                        FiniteNumber(p, "heightMm", true));
                case CadCommandNames.AddCircle:
                    return First(
                        Only(p, "centerXmm", "centerYmm", "diameterMm"),
                        FiniteNumber(p, "centerXmm", false),
                        FiniteNumber(p, "centerYmm", false),
                        FiniteNumber(p, "diameterMm", true));
                case CadCommandNames.AddSlot:
                    return PrismaticProfileGeometry.ValidateSlot(p);
                case CadCommandNames.AddRegularPolygon:
                    return PrismaticProfileGeometry.ValidateRegularPolygon(p);
                case CadCommandNames.Extrude:
                    return First(Only(p, "depthMm"), FiniteNumber(p, "depthMm", true));
                case CadCommandNames.CutExtrude:
                    return PrismaticProfileGeometry.ValidateCut(p);
                default:
                    return "The proposed CAD plan contains an unsupported command: " + command.Command;
            }
        }

        public static bool RequestsOverwrite(IEnumerable<CadCommandEnvelope> commands)
        {
            return commands != null && commands.Any(command =>
                command?.Command == CadCommandNames.SavePart &&
                command.Parameters?["allowOverwrite"]?.Type == JTokenType.Boolean &&
                command.Parameters["allowOverwrite"].Value<bool>());
        }

        private static string Only(JObject value, params string[] names)
        {
            var unexpected = value.Properties().FirstOrDefault(property => Array.IndexOf(names, property.Name) < 0);
            return unexpected == null ? null : "Unexpected parameter '" + unexpected.Name + "'.";
        }

        private static string RequiredString(JObject value, string name) =>
            value[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)value[name])
                ? null
                : name + " must be a non-empty string.";

        private static string OptionalBoolean(JObject value, string name) =>
            value[name] == null || value[name].Type == JTokenType.Boolean
                ? null
                : name + " must be a boolean when supplied.";

        private static string FiniteNumber(JObject value, string name, bool positive)
        {
            if (value[name] == null || (value[name].Type != JTokenType.Integer && value[name].Type != JTokenType.Float))
                return name + " must be a number.";
            var number = value[name].Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || (positive && number <= 0.0))
                return name + (positive ? " must be a finite number greater than zero." : " must be a finite number.");
            return null;
        }

        private static string First(params string[] errors) => errors.FirstOrDefault(error => error != null);
    }
}
