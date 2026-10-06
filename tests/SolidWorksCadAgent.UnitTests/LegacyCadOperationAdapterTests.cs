using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class LegacyCadOperationAdapterTests
    {
        [TestMethod]
        public void SupportedLegacySubsetAdaptsToTypedValuesAndRoundTripsExactly()
        {
            Assert.IsTrue(LegacyCadOperationAdapter.TryAdapt(Command(CadCommandNames.NewPart, new JObject()), out var newPart, out var error));
            Assert.IsNull(error);
            Assert.IsInstanceOfType(newPart, typeof(NewPartOperation));
            Assert.AreEqual(CadCommandNames.NewPart, newPart.ToCommandEnvelope().Command);
            Assert.AreEqual(1, newPart.OperationVersion);

            Assert.IsTrue(LegacyCadOperationAdapter.TryAdapt(Command(CadCommandNames.CreateSketch, JObject.FromObject(new { plane = "Front Plane" })), out var sketch, out error));
            Assert.IsNull(error);
            var typedSketch = (CreateSketchOperation)sketch;
            Assert.AreEqual(SketchPlane.Front, typedSketch.Plane);
            Assert.AreEqual("Front Plane", (string)typedSketch.ToCommandEnvelope().Parameters["plane"]);

            var source = Command(CadCommandNames.AddCircle, JObject.FromObject(new { centerXmm = -3.25, centerYmm = 8.5, diameterMm = 12.0 }));
            Assert.IsTrue(LegacyCadOperationAdapter.TryAdapt(source, out var circle, out error));
            Assert.IsNull(error);
            var typedCircle = (AddCircleOperation)circle;
            Assert.AreEqual(-3.25, typedCircle.Center.X.Value);
            Assert.AreEqual(8.5, typedCircle.Center.Y.Value);
            Assert.AreEqual(12.0, typedCircle.Diameter.Value);
            Assert.AreEqual(source.Parameters.ToString(), typedCircle.ToCommandEnvelope().Parameters.ToString());

            var lineSource = Command(CadCommandNames.AddLine, JObject.FromObject(new
            {
                startXmm = -12.5, startYmm = 3.0, endXmm = 4.25, endYmm = 9.5
            }));
            Assert.IsTrue(LegacyCadOperationAdapter.TryAdapt(lineSource, out var line, out error));
            Assert.IsNull(error);
            var typedLine = (AddLineOperation)line;
            Assert.AreEqual(-12.5, typedLine.Start.X.Value);
            Assert.AreEqual(3.0, typedLine.Start.Y.Value);
            Assert.AreEqual(4.25, typedLine.End.X.Value);
            Assert.AreEqual(9.5, typedLine.End.Y.Value);
            Assert.AreEqual(lineSource.Parameters.ToString(), typedLine.ToCommandEnvelope().Parameters.ToString());
        }

        [TestMethod]
        public void InvalidTypedParametersFailAndAreNotSilentlyAdapted()
        {
            var invalid = Command(CadCommandNames.AddCircle, JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, diameterMm = 0.0 }));
            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(invalid, out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNotNull(error);
            Assert.AreEqual("INVALID_PARAMETERS", error.Code);
        }

        [TestMethod]
        public void DegenerateLineFailsTypedAdaptation()
        {
            var zeroLength = Command(CadCommandNames.AddLine, JObject.FromObject(new
            {
                startXmm = 1.0, startYmm = 2.0, endXmm = 1.0, endYmm = 2.0
            }));

            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(zeroLength, out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNotNull(error);
            Assert.AreEqual("INVALID_PARAMETERS", error.Code);
            StringAssert.Contains(error.Message, "Start and end");
        }

        [TestMethod]
        public void LineAtMinimumSeparationThresholdFailsTypedAdaptation()
        {
            var tooShort = Command(CadCommandNames.AddLine, new JObject
            {
                ["startXmm"] = 0.0,
                ["startYmm"] = 0.0,
                ["endXmm"] = 0.000001,
                ["endYmm"] = 0.0
            });

            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(tooShort, out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNotNull(error);
            Assert.AreEqual("INVALID_PARAMETERS", error.Code);
            StringAssert.Contains(error.Message, "0.000001 mm");
        }

        [TestMethod]
        public void NonFiniteLineCoordinateFailsTypedAdaptation()
        {
            var invalid = new JObject
            {
                ["startXmm"] = 0.0,
                ["startYmm"] = 0.0,
                ["endXmm"] = double.PositiveInfinity,
                ["endYmm"] = 1.0
            };

            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(Command(CadCommandNames.AddLine, invalid), out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNotNull(error);
            Assert.AreEqual("INVALID_PARAMETERS", error.Code);
            StringAssert.Contains(error.Message, "finite");
        }

        [TestMethod]
        public void AdapterDoesNotReadVersionFromLegacyPayload()
        {
            var payload = JObject.FromObject(new { plane = "Top Plane", operationVersion = 2 });
            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(Command(CadCommandNames.CreateSketch, payload), out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNotNull(error);
            Assert.AreEqual("INVALID_PARAMETERS", error.Code);
        }

        [TestMethod]
        public void ValidCatalogOperationsOutsideTypedSubsetRemainForLegacyFallback()
        {
            var legacy = Command(CadCommandNames.AddArc, JObject.FromObject(new
            {
                centerXmm = 0.0, centerYmm = 0.0,
                startXmm = 10.0, startYmm = 0.0, endXmm = 0.0, endYmm = 10.0,
                clockwise = false
            }));
            Assert.IsFalse(LegacyCadOperationAdapter.TryAdapt(legacy, out var operation, out var error));
            Assert.IsNull(operation);
            Assert.IsNull(error);
            Assert.IsNull(CadPlanningCommandContract.Validate(legacy));
        }

        private static CadCommandEnvelope Command(string name, JObject parameters) =>
            new CadCommandEnvelope { Command = name, Parameters = parameters };
    }
}
