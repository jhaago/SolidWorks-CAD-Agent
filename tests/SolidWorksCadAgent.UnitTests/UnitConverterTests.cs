using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class UnitConverterTests
    {
        private static Type GetUnitConverterType()
        {
            return Type.GetType(
                "SolidWorksCadAgent.Core.Units.UnitConverter, SolidWorksCadAgent.Core",
                throwOnError: false);
        }

        [TestMethod]
        public void MillimetresToMetres_100mm_ReturnsPointOneMetre()
        {
            var type = GetUnitConverterType();
            Assert.IsNotNull(type, "UnitConverter type must exist in SolidWorksCadAgent.Core.");

            var method = type.GetMethod("MillimetresToMetres", new[] { typeof(double) });
            Assert.IsNotNull(method, "MillimetresToMetres(double) must exist.");

            var result = (double)method.Invoke(null, new object[] { 100.0 });
            Assert.AreEqual(0.1, result, 1e-12);
        }

        [TestMethod]
        public void MetresToMillimetres_PointZeroOne_Returns10mm()
        {
            var type = GetUnitConverterType();
            Assert.IsNotNull(type, "UnitConverter type must exist in SolidWorksCadAgent.Core.");

            var method = type.GetMethod("MetresToMillimetres", new[] { typeof(double) });
            Assert.IsNotNull(method, "MetresToMillimetres(double) must exist.");

            var result = (double)method.Invoke(null, new object[] { 0.01 });
            Assert.AreEqual(10.0, result, 1e-12);
        }
    }
}
