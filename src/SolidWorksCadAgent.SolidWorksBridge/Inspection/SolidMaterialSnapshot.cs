using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    /// <summary>Measured solid volume only; density is deliberately not used as evidence.</summary>
    public sealed class SolidMaterialSnapshot
    {
        private SolidMaterialSnapshot(int count, double volumeMm3) { BodyCount = count; VolumeMm3 = volumeMm3; }
        public int BodyCount { get; }
        public double VolumeMm3 { get; }

        public static SolidMaterialSnapshot FromBodyMassProperties(IEnumerable<double[]> properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            return FromBodyVolumesMetresCubed(properties.Select(mass =>
            {
                // IBody2.GetMassProperties: 12 doubles for a solid body; volume is index 3.
                if (mass == null || mass.Length != 12 || mass.Any(value => !Finite(value)))
                    throw new ArgumentException("Native solid mass properties must contain all 12 finite values.", nameof(properties));
                return mass[3];
            }));
        }

        public static SolidMaterialSnapshot FromBodyVolumesMetresCubed(IEnumerable<double> volumes)
        {
            if (volumes == null) throw new ArgumentNullException(nameof(volumes));
            int count = 0; double total = 0;
            foreach (var volume in volumes)
            {
                double mm3 = volume * 1e9;
                if (!Finite(volume) || volume <= 0 || !Finite(mm3) || mm3 <= 0 || !Finite(total + mm3))
                    throw new ArgumentOutOfRangeException(nameof(volumes), "Every solid requires a finite positive native volume and representable mm³ total.");
                total += mm3; count = checked(count + 1);
            }
            return new SolidMaterialSnapshot(count, total);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
