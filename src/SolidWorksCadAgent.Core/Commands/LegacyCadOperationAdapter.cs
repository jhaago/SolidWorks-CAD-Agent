using System;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>
    /// Lowers the legacy JSON envelope into the initial typed operation subset. Other catalogued
    /// commands remain on the existing envelope path until their typed operations are introduced.
    /// </summary>
    public static class LegacyCadOperationAdapter
    {
        public static bool TryAdapt(CadCommandEnvelope legacy, out CadOperation operation, out CadError error)
        {
            operation = null;
            error = null;
            if (legacy == null)
            {
                error = Invalid("A CAD command is required.");
                return false;
            }

            CadOperationDescriptor descriptor;
            switch (legacy.Command)
            {
                case CadCommandNames.NewPart:
                    descriptor = CadOperationCatalog.Find(CadCommandNames.NewPart);
                    break;
                case CadCommandNames.CreateSketch:
                    descriptor = CadOperationCatalog.Find(CadCommandNames.CreateSketch);
                    break;
                case CadCommandNames.AddLine:
                    descriptor = CadOperationCatalog.Find(CadCommandNames.AddLine);
                    break;
                case CadCommandNames.AddCircle:
                    descriptor = CadOperationCatalog.Find(CadCommandNames.AddCircle);
                    break;
                default:
                    return false;
            }

            var validationError = CadPlanningCommandContract.Validate(legacy);
            if (validationError != null)
            {
                error = Invalid(validationError);
                return false;
            }

            var parameters = legacy.Parameters ?? new JObject();
            switch (legacy.Command)
            {
                case CadCommandNames.NewPart:
                    operation = new NewPartOperation(descriptor);
                    return true;
                case CadCommandNames.CreateSketch:
                    operation = new CreateSketchOperation(descriptor, ParsePlane((string)parameters["plane"]));
                    return true;
                case CadCommandNames.AddLine:
                    operation = new AddLineOperation(
                        descriptor,
                        new Point2Mm(
                            new LengthMm(parameters["startXmm"].Value<double>()),
                            new LengthMm(parameters["startYmm"].Value<double>())),
                        new Point2Mm(
                            new LengthMm(parameters["endXmm"].Value<double>()),
                            new LengthMm(parameters["endYmm"].Value<double>())));
                    return true;
                case CadCommandNames.AddCircle:
                    operation = new AddCircleOperation(
                        descriptor,
                        new Point2Mm(
                            new LengthMm(parameters["centerXmm"].Value<double>()),
                            new LengthMm(parameters["centerYmm"].Value<double>())),
                        new LengthMm(parameters["diameterMm"].Value<double>()));
                    return true;
                default:
                    return false;
            }
        }

        private static SketchPlane ParsePlane(string value)
        {
            switch (value)
            {
                case "Top Plane": return SketchPlane.Top;
                case "Front Plane": return SketchPlane.Front;
                case "Right Plane": return SketchPlane.Right;
                default: throw new InvalidOperationException("The validated sketch plane is not mapped to a typed operation.");
            }
        }

        private static CadError Invalid(string message) => new CadError
        {
            Code = "INVALID_PARAMETERS",
            Stage = "AdaptLegacyCommand",
            Message = message
        };
    }
}
