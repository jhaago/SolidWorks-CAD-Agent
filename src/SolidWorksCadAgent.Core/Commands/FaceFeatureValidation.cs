using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    // Validates the public selector protocol only; topology and unique-face checks belong to the bridge.
    public static class FaceFeatureValidation
    {
        public static string ValidateFaceSketch(JObject parameters) => ValidateSelector(parameters, null);
        public static string ValidateFillet(JObject parameters) => ValidateFinishing(parameters, "radiusMm");
        public static string ValidateChamfer(JObject parameters) => ValidateFinishing(parameters, "distanceMm");

        private static string ValidateSelector(JObject parameters, string dimension)
        {
            if (parameters == null) return "Face selector parameters are required.";
            if (parameters.Properties().Any(p => p.Name != "normalAxis" && p.Name != "side" && p.Name != dimension))
                return "Unexpected face feature parameter.";
            if (parameters["normalAxis"]?.Type != JTokenType.String ||
                !new[] { "X", "Y", "Z" }.Contains((string)parameters["normalAxis"]))
                return "normalAxis must be X, Y, or Z.";
            if (parameters["side"]?.Type != JTokenType.String ||
                !new[] { "Min", "Max" }.Contains((string)parameters["side"]))
                return "side must be Min or Max.";
            return null;
        }

        private static string ValidateFinishing(JObject parameters, string dimension)
        {
            var error = ValidateSelector(parameters, dimension);
            if (error != null) return error;
            if (parameters[dimension]?.Type != JTokenType.Integer && parameters[dimension]?.Type != JTokenType.Float)
                return dimension + " must be a number.";
            try
            {
                double millimetres = parameters[dimension].Value<double>();
                double metres = millimetres / 1000.0;
                return double.IsNaN(millimetres) || double.IsInfinity(millimetres) ||
                    double.IsNaN(metres) || double.IsInfinity(metres) || metres <= 0
                    ? dimension + " must remain finite and positive after conversion to metres." : null;
            }
            catch (Exception e) when (e is OverflowException || e is FormatException || e is InvalidCastException)
            {
                return dimension + " must be a finite representable number.";
            }
        }
    }
}
