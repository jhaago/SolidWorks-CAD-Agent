using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.TestUtilities;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AcceptancePlateEvidenceTests
    {
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void FullHole_WithTwoOuterFaceOpeningsAndExpectedVolume_IsAccepted(int thicknessAxis)
        {
            Assert.IsTrue(Plate(thicknessAxis).IsCentredTwentyMillimetreThroughHole());
        }

        [TestMethod]
        public void BlindHole_EvenWithACutFeature_IsRejected()
        {
            var plate = Plate();
            plate.Openings.RemoveAt(1);
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.Openings[1].CentreMm[2] = 9; // Ends inside the plate rather than on the opposite face.
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
        }

        [TestMethod]
        public void WrongDiameterOrOffsetOpening_IsRejected()
        {
            var plate = Plate();
            plate.Openings[0].RadiusMm = 9;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.Openings[1].CentreMm[0] = 1;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
        }

        [TestMethod]
        public void MissingOrPartialCylindricalWall_IsRejected()
        {
            var plate = Plate();
            plate.CylindricalFaceCount = 0;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.CylindricalAreaMm2 /= 2;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.CylindricalRadiusMm = 11;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
        }

        [TestMethod]
        public void NoRemovedMaterialOrNonFiniteEvidence_IsRejected()
        {
            var plate = Plate();
            plate.VolumeMm3 = 60000;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.VolumeMm3 = double.NaN;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
            plate = Plate();
            plate.Openings[0].Normal[2] = double.NaN;
            Assert.IsFalse(plate.IsCentredTwentyMillimetreThroughHole());
        }

        private static AcceptancePlateEvidence Plate(int thicknessAxis = 2)
        {
            var min = new double[] { -50, -30, 0 };
            var max = new double[] { 50, 30, 10 };
            var first = new double[] { 0, 0, 0 };
            var second = new double[] { 0, 0, 10 };
            var normal = new double[] { 0, 0, 1 };
            Swap(min, thicknessAxis); Swap(max, thicknessAxis);
            Swap(first, thicknessAxis); Swap(second, thicknessAxis); Swap(normal, thicknessAxis);
            return new AcceptancePlateEvidence
            {
                MinMm = min, MaxMm = max, VolumeMm3 = 60000 - Math.PI * 100 * 10,
                FaceCount = 7, CylindricalFaceCount = 1, CylindricalRadiusMm = 10,
                CylindricalAreaMm2 = 2 * Math.PI * 10 * 10,
                Openings = new System.Collections.Generic.List<CircularOpeningEvidence>
                {
                    new CircularOpeningEvidence { CentreMm = first, Normal = normal, RadiusMm = 10 },
                    new CircularOpeningEvidence { CentreMm = second, Normal = normal, RadiusMm = 10 }
                }
            };
        }
        private static void Swap(double[] values, int axis) { var temp = values[axis]; values[axis] = values[2]; values[2] = temp; }
    }
}
