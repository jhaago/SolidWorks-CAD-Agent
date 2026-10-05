using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksCadAgent.TestUtilities
{
    // Only the fixed 100 x 60 x 10 plate fixture is supported; this is not a CAD command.
    public sealed class CircularOpeningEvidence
    {
        public double[] CentreMm { get; set; }
        public double[] Normal { get; set; }
        public double RadiusMm { get; set; }
    }

    public sealed class AcceptancePlateEvidence
    {
        public double[] MinMm { get; set; }
        public double[] MaxMm { get; set; }
        public double VolumeMm3 { get; set; }
        public int FaceCount { get; set; }
        public int CylindricalFaceCount { get; set; }
        public double CylindricalRadiusMm { get; set; }
        public double CylindricalAreaMm2 { get; set; }
        public List<CircularOpeningEvidence> Openings { get; set; } = new List<CircularOpeningEvidence>();

        public bool IsCentredTwentyMillimetreThroughHole()
        {
            if (!VectorIsFinite(MinMm) || !VectorIsFinite(MaxMm) || Openings == null || Openings.Count != 2)
                return false;
            var sizes = Enumerable.Range(0, 3).Select(axis => MaxMm[axis] - MinMm[axis]).ToArray();
            var sorted = sizes.OrderBy(size => size).ToArray();
            if (!Near(sorted[0], 10, 0.02) || !Near(sorted[1], 60, 0.02) || !Near(sorted[2], 100, 0.02))
                return false;
            var axisOfThickness = Array.IndexOf(sizes, sorted[0]);
            if (FaceCount != 7 || CylindricalFaceCount != 1 || !Near(CylindricalRadiusMm, 10, 0.02) ||
                !Near(CylindricalAreaMm2, 200 * Math.PI, 0.1) || !Near(VolumeMm3, 60000 - 1000 * Math.PI, 0.5))
                return false;
            var ends = new bool[2];
            foreach (var opening in Openings)
            {
                if (opening == null || !VectorIsFinite(opening.CentreMm) || !VectorIsFinite(opening.Normal) ||
                    !Near(opening.RadiusMm, 10, 0.02)) return false;
                for (var axis = 0; axis < 3; axis++)
                {
                    if (!Near(Math.Abs(opening.Normal[axis]), axis == axisOfThickness ? 1 : 0, 1e-6)) return false;
                    if (axis != axisOfThickness && !Near(opening.CentreMm[axis], (MinMm[axis] + MaxMm[axis]) / 2, 0.02))
                        return false;
                }
                if (Near(opening.CentreMm[axisOfThickness], MinMm[axisOfThickness], 0.02)) ends[0] = true;
                else if (Near(opening.CentreMm[axisOfThickness], MaxMm[axisOfThickness], 0.02)) ends[1] = true;
                else return false;
            }
            return ends[0] && ends[1];
        }

        private static bool VectorIsFinite(double[] values) => values != null && values.Length == 3 &&
            values.All(value => !double.IsNaN(value) && !double.IsInfinity(value));
        private static bool Near(double actual, double expected, double tolerance) =>
            !double.IsNaN(actual) && !double.IsInfinity(actual) && Math.Abs(actual - expected) <= tolerance;
    }
}
