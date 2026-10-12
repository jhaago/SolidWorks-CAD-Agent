using System;

namespace SolidWorksCadAgent.Core.Units
{
    public static class UnitConverter
    {
        public static double MillimetresToMetres(double millimetres)
        {
            EnsureFinite(millimetres, nameof(millimetres));
            return millimetres / 1000.0;
        }

        public static double MetresToMillimetres(double metres)
        {
            EnsureFinite(metres, nameof(metres));
            return metres * 1000.0;
        }

        private static void EnsureFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "CAD dimensions must be finite numbers.");
            }
        }
    }
}
