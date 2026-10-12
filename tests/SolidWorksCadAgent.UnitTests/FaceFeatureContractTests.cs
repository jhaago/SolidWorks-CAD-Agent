using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FaceFeatureContractTests
    {
        private static CadCommandEnvelope Command(string name, JObject p) => new CadCommandEnvelope { Command = name, Parameters = p };

        [TestMethod]
        public void IncompleteFaceSketchAndFinishingCommandsAreNotAdvertised()
        {
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command("CreateSketchOnFace", JObject.FromObject(new { normalAxis = "Z", side = "Max" }))));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command("FilletEdges", JObject.FromObject(new { normalAxis = "Z", side = "Max", radiusMm = 2 }))));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command("ChamferEdges", JObject.FromObject(new { normalAxis = "X", side = "Min", distanceMm = 1 }))));
            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("CreateSketchOnFace"));
            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("FilletEdges"));
            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("ChamferEdges"));
        }
        [DataTestMethod]
        [DataRow("X", "Min")]
        [DataRow("Y", "Max")]
        [DataRow("Z", "Min")]
        public void ExactFaceSelectorsAreValid(string axis, string side)
        {
            Assert.IsNull(FaceFeatureValidation.ValidateFaceSketch(JObject.FromObject(new { normalAxis = axis, side })));
        }

        [DataTestMethod]
        [DataRow("axisMissing")]
        [DataRow("sideMissing")]
        [DataRow("axisCase")]
        [DataRow("sideCase")]
        [DataRow("axisType")]
        [DataRow("sideType")]
        [DataRow("extra")]
        public void FaceSelectorsRejectWrongTypesValuesAndUnexpectedFields(string kind)
        {
            var p = JObject.FromObject(new { normalAxis = "Z", side = "Max" });
            if (kind == "axisMissing") p.Remove("normalAxis");
            if (kind == "sideMissing") p.Remove("side");
            if (kind == "axisCase") p["normalAxis"] = "z";
            if (kind == "sideCase") p["side"] = "max";
            if (kind == "axisType") p["normalAxis"] = 2;
            if (kind == "sideType") p["side"] = true;
            if (kind == "extra") p["faceIndex"] = 0;
            Assert.IsNotNull(FaceFeatureValidation.ValidateFaceSketch(p));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command("CreateSketchOnFace", p)));
        }

        [DataTestMethod]
        [DataRow("FilletEdges", "radiusMm")]
        [DataRow("ChamferEdges", "distanceMm")]
        public void FinishingRequiresFinitePositiveRepresentableDimensions(string command, string dimension)
        {
            var p = JObject.FromObject(new { normalAxis = "Z", side = "Max" });
            foreach (var invalid in new JToken[] { new JValue(0), new JValue(-1), new JValue("1"), new JValue(true), JValue.CreateNull(), new JValue(double.NaN), new JValue(double.PositiveInfinity), new JValue(double.Epsilon) })
            {
                p[dimension] = invalid;
                Assert.IsNotNull(command == "FilletEdges"
                    ? FaceFeatureValidation.ValidateFillet(p)
                    : FaceFeatureValidation.ValidateChamfer(p));
            }
            p.Remove(dimension);
            Assert.IsNotNull(command == "FilletEdges"
                ? FaceFeatureValidation.ValidateFillet(p)
                : FaceFeatureValidation.ValidateChamfer(p));
            p[dimension] = double.Epsilon * 1000;
            Assert.IsNull(command == "FilletEdges"
                ? FaceFeatureValidation.ValidateFillet(p)
                : FaceFeatureValidation.ValidateChamfer(p));
            p[dimension] = 2;
            p["angleDegrees"] = 45;
            Assert.IsNotNull(command == "FilletEdges"
                ? FaceFeatureValidation.ValidateFillet(p)
                : FaceFeatureValidation.ValidateChamfer(p));
        }

        [TestMethod]
        public void SharedValidationRejectsNullAndWrongFinishingDimensions()
        {
            Assert.IsNotNull(FaceFeatureValidation.ValidateFaceSketch(null));
            Assert.IsNotNull(FaceFeatureValidation.ValidateFillet(null));
            Assert.IsNotNull(FaceFeatureValidation.ValidateChamfer(null));
            Assert.IsNotNull(FaceFeatureValidation.ValidateFillet(JObject.FromObject(new { normalAxis = "Z", side = "Max", distanceMm = 2 })));
            Assert.IsNotNull(FaceFeatureValidation.ValidateChamfer(JObject.FromObject(new { normalAxis = "Z", side = "Max", radiusMm = 2 })));
        }

        [TestMethod]
        public void ProtocolDoesNotPromiseIncompleteFaceOrDirectionSemantics()
        {
            var text = CadPlanningCommandContract.ProtocolDescription;
            Assert.IsFalse(text.Contains("CreateSketchOnFace"));
            Assert.IsFalse(text.Contains("FilletEdges"));
            Assert.IsFalse(text.Contains("ChamferEdges"));
            Assert.IsFalse(text.Contains("IntoBody"));
        }    }
}
