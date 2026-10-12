using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadResultJsonRegressionTests
    {
        [TestMethod]
        public void Bounds_UsesExactCamelCaseNamesAndMillimetreValues()
        {
            var data = CadCommandResult.Ok(new PreciseBoundingBox
            {
                MinXmm = -50, MaxXmm = 50, MinYmm = -30, MaxYmm = 30, MinZmm = 0, MaxZmm = 10
            }).Data;
            CollectionAssert.AreEquivalent(new[] { "minXmm", "minYmm", "minZmm", "maxXmm", "maxYmm", "maxZmm", "sizeXmm", "sizeYmm", "sizeZmm" },
                System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(data.Properties(), p => p.Name)));
            Assert.AreEqual(100.0, data.Value<double>("sizeXmm"));
            Assert.AreEqual(60.0, data.Value<double>("sizeYmm"));
            Assert.AreEqual(10.0, data.Value<double>("sizeZmm"));
            Assert.IsNull(data["SizeXmm"]);
        }

        [TestMethod]
        public void Inspection_UsesCamelCaseIncludingNestedFeatureProperties()
        {
            var feature = new FeatureInspectionItem { Name = "Cut-Extrude1", TypeName = "Cut", ErrorCode = 0, IsWarning = false };
            var data = CadCommandResult.Ok(new RebuildInspectionResult
            {
                Rebuilt = true, HasErrors = false, HasWarnings = false, Features = new[] { feature }
            }).Data;
            Assert.IsTrue(data.Value<bool>("rebuilt"));
            Assert.IsFalse(data.Value<bool>("hasErrors"));
            Assert.IsFalse(data.Value<bool>("hasWarnings"));
            var item = data["features"][0];
            CollectionAssert.AreEquivalent(new[] { "name", "typeName", "errorCode", "isWarning" },
                System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(((JObject)item).Properties(), p => p.Name)));
            Assert.AreEqual("Cut", (string)item["typeName"]);
            Assert.AreEqual(0, (int)item["errorCode"]);
            Assert.AreEqual(1, CadCommandResult.Ok(new { BodyCount = 1 }).Data.Value<int>("bodyCount"));
            Assert.AreEqual("Cut", (string)CadCommandResult.Ok(new { Features = new[] { feature } }).Data["features"][0]["typeName"]);
        }
    }
}
