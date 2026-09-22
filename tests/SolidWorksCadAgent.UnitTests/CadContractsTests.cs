using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadContractsTests
    {
        private const string ContractsAssembly = "SolidWorksCadAgent.Contracts";

        private static Type RequireType(string fullName)
        {
            var type = Type.GetType(fullName + ", " + ContractsAssembly, throwOnError: false);
            Assert.IsNotNull(type, fullName + " must exist.");
            return type;
        }

        [TestMethod]
        public void CadCommandEnvelope_ExposesCommandAndParameters()
        {
            var type = RequireType("SolidWorksCadAgent.Contracts.Cad.CadCommandEnvelope");
            Assert.AreEqual(typeof(string), type.GetProperty("Command")?.PropertyType);
            Assert.AreEqual("Newtonsoft.Json.Linq.JObject", type.GetProperty("Parameters")?.PropertyType.FullName);
        }

        [TestMethod]
        public void CadCommandResult_ExposesStructuredSuccessDataAndError()
        {
            var resultType = RequireType("SolidWorksCadAgent.Contracts.Cad.CadCommandResult");
            var errorType = RequireType("SolidWorksCadAgent.Contracts.Cad.CadError");

            Assert.AreEqual(typeof(bool), resultType.GetProperty("Success")?.PropertyType);
            Assert.AreEqual("Newtonsoft.Json.Linq.JObject", resultType.GetProperty("Data")?.PropertyType.FullName);
            Assert.AreEqual(errorType, resultType.GetProperty("Error")?.PropertyType);
            Assert.IsNotNull(resultType.GetMethod("Ok", new[] { typeof(object) }));
        }

        [TestMethod]
        public void CadCommandNames_DefinesInitialV1CommandsExactly()
        {
            var type = RequireType("SolidWorksCadAgent.Contracts.Cad.CadCommandNames");
            var expected = new[]
            {
                "AttachSolidWorks", "LaunchSolidWorks", "NewPart", "OpenPart", "SavePart", "CloseDocument",
                "CreateSketch", "AddRectangle", "AddCircle", "ExitSketch", "Extrude",
                "CutExtrude", "Rebuild", "GetBoundingBox", "GetBodyCount", "GetRebuildErrors", "GetFeatureTree"
            };

            var values = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .ToArray();

            CollectionAssert.AreEquivalent(expected, values);
        }

        [TestMethod]
        public void GeometryDtos_ExposeMillimetreBasedAcceptanceParameters()
        {
            var rectangle = RequireType("SolidWorksCadAgent.Contracts.Cad.RectangleParameters");
            Assert.AreEqual(typeof(double), rectangle.GetProperty("CenterXmm")?.PropertyType);
            Assert.AreEqual(typeof(double), rectangle.GetProperty("CenterYmm")?.PropertyType);
            Assert.AreEqual(typeof(double), rectangle.GetProperty("WidthMm")?.PropertyType);
            Assert.AreEqual(typeof(double), rectangle.GetProperty("HeightMm")?.PropertyType);

            var circle = RequireType("SolidWorksCadAgent.Contracts.Cad.CircleParameters");
            Assert.AreEqual(typeof(double), circle.GetProperty("DiameterMm")?.PropertyType);

            var extrude = RequireType("SolidWorksCadAgent.Contracts.Cad.ExtrudeParameters");
            Assert.AreEqual(typeof(double), extrude.GetProperty("DepthMm")?.PropertyType);
        }
    }
}
