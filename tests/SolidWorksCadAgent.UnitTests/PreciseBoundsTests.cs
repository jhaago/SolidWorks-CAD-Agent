using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class PreciseBoundsTests
    {
        [TestMethod]
        public void SinglePlateExtents_Returns100By60By10Millimetres()
        {
            var accumulator = new PreciseBoundsAccumulator();
            accumulator.IncludeBodyExtentsMetres(
                -0.050, -0.030, 0.000,
                 0.050,  0.030, 0.010);

            var bounds = accumulator.ToMillimetres();

            Assert.AreEqual(-50.0, bounds.MinXmm, 1e-9);
            Assert.AreEqual(-30.0, bounds.MinYmm, 1e-9);
            Assert.AreEqual(0.0, bounds.MinZmm, 1e-9);
            Assert.AreEqual(50.0, bounds.MaxXmm, 1e-9);
            Assert.AreEqual(30.0, bounds.MaxYmm, 1e-9);
            Assert.AreEqual(10.0, bounds.MaxZmm, 1e-9);
            Assert.AreEqual(100.0, bounds.SizeXmm, 1e-9);
            Assert.AreEqual(60.0, bounds.SizeYmm, 1e-9);
            Assert.AreEqual(10.0, bounds.SizeZmm, 1e-9);
        }

        [TestMethod]
        public void MultipleBodyExtents_AreUnionedBeforeMillimetreConversion()
        {
            var accumulator = new PreciseBoundsAccumulator();
            accumulator.IncludeBodyExtentsMetres(-0.010, -0.020, -0.003, 0.015, 0.020, 0.004);
            accumulator.IncludeBodyExtentsMetres(-0.025, -0.005, -0.001, 0.005, 0.030, 0.009);

            var bounds = accumulator.ToMillimetres();

            Assert.AreEqual(-25.0, bounds.MinXmm, 1e-9);
            Assert.AreEqual(-20.0, bounds.MinYmm, 1e-9);
            Assert.AreEqual(-3.0, bounds.MinZmm, 1e-9);
            Assert.AreEqual(15.0, bounds.MaxXmm, 1e-9);
            Assert.AreEqual(30.0, bounds.MaxYmm, 1e-9);
            Assert.AreEqual(9.0, bounds.MaxZmm, 1e-9);
            Assert.AreEqual(40.0, bounds.SizeXmm, 1e-9);
            Assert.AreEqual(50.0, bounds.SizeYmm, 1e-9);
            Assert.AreEqual(12.0, bounds.SizeZmm, 1e-9);
        }

        [TestMethod]
        public void ToMillimetres_WithoutBodies_Throws()
        {
            var accumulator = new PreciseBoundsAccumulator();
            Assert.ThrowsException<InvalidOperationException>(() => accumulator.ToMillimetres());
        }

        [TestMethod]
        public void IncludeBodyExtents_NonFiniteValue_Throws()
        {
            var accumulator = new PreciseBoundsAccumulator();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                accumulator.IncludeBodyExtentsMetres(0, 0, 0, double.NaN, 1, 1));
        }

        [TestMethod]
        public void IncludeBodyExtents_InvertedAxis_Throws()
        {
            var accumulator = new PreciseBoundsAccumulator();
            Assert.ThrowsException<ArgumentException>(() =>
                accumulator.IncludeBodyExtentsMetres(2, 0, 0, 1, 1, 1));
        }
    }
}
