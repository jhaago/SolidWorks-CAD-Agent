using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadV2PrismaticGeometryTests
    {
        [TestMethod]
        public void OffsetRectangleRetainsFiveStepSupportAndModelAxisBounds()
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7, centerYmm = -3, widthMm = 20, heightMm = 10 });
            var result = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(1000, result.VolumeMm3, 1e-9);
            Assert.AreEqual(5, result.DepthMm, 1e-9);
            Assert.IsNull(result.SaveStep);
            Assert.AreEqual("feature1", result.LastFeatureStepKey);
            var expected = JObject.Parse("{minXmm:-3,maxXmm:17,minYmm:0,maxYmm:5,minZmm:-2,maxZmm:8,sizeXmm:20,sizeYmm:5,sizeZmm:10}");
            foreach (var property in expected.Properties())
                Assert.AreEqual(property.Value.Value<double>(), result.ExpectedBounds()[property.Name].Value<double>(), 1e-9, property.Name);
        }

        [TestMethod]
        public void CircleBossUsesCircularAreaAndOffsetBounds()
        {
            var result = CadV2PrismaticGeometry.Analyze(Plan("AddCircle", new { centerXmm = 3, centerYmm = 4, diameterMm = 8 }));
            Assert.AreEqual(251.32741228718345, result.VolumeMm3, 1e-9);
            Assert.AreEqual(-1, result.MinXmm, 1e-9);
            Assert.AreEqual(-8, result.MinZmm, 1e-9);
            Assert.AreEqual(0, result.MaxZmm, 1e-9);
        }

        [TestMethod]
        public void RotatedSlotBossIncludesSemicircleExtents()
        {
            var result = CadV2PrismaticGeometry.Analyze(Plan("AddSlot", new { centerXmm = 2, centerYmm = 3, lengthMm = 10, widthMm = 4, angleDegrees = 30 }));
            Assert.AreEqual(182.83185307179586, result.VolumeMm3, 1e-9);
            Assert.AreEqual(-2.598076211353316, result.MinXmm, 1e-9);
            Assert.AreEqual(6.598076211353316, result.MaxXmm, 1e-9);
            Assert.AreEqual(-6.5, result.MinZmm, 1e-9);
            Assert.AreEqual(0.5, result.MaxZmm, 1e-9);
        }

        [TestMethod]
        public void RotatedTriangleBoundsUseVerticesNotCircumcircle()
        {
            var result = CadV2PrismaticGeometry.Analyze(Plan("AddRegularPolygon", new { centerXmm = 2, centerYmm = 3, sides = 3, diameterMm = 8, angleDegrees = 30 }));
            Assert.AreEqual(103.92304845413264, result.VolumeMm3, 1e-9);
            Assert.AreEqual(-1.4641016151377544, result.MinXmm, 1e-9);
            Assert.AreEqual(5.4641016151377544, result.MaxXmm, 1e-9);
            Assert.AreEqual(-5, result.MinZmm, 1e-9);
            Assert.AreEqual(1, result.MaxZmm, 1e-9);
        }

        [TestMethod]
        public void DisjointThroughAndBlindCutsSubtractTheirOwnDepthsAndRetainOuterBounds()
        {
            var plan = Plate();
            Group(plan, "AddCircle", new { centerXmm = -8, centerYmm = 0, diameterMm = 4 }, new { endCondition = "ThroughAll" });
            Group(plan, "AddRectangle", new { centerXmm = 8, centerYmm = 0, widthMm = 4, heightMm = 6 }, new { endCondition = "Blind", depthMm = 2 });
            var save = Step("save", "SavePart", new { path = "part.sldprt", allowOverwrite = false });
            plan.Steps.Add(save);
            var result = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(3889.168146928204, result.VolumeMm3, 1e-9);
            Assert.AreEqual(-20, result.MinXmm, 1e-9);
            Assert.AreEqual(10, result.MaxZmm, 1e-9);
            Assert.AreEqual("feature3", result.LastFeatureStepKey);
            Assert.AreSame(save, result.SaveStep);
        }

        [DataTestMethod]
        [DataRow("AddSlot", "{centerXmm:0,centerYmm:0,lengthMm:10,widthMm:4,angleDegrees:90}", 1817.168146928204)]
        [DataRow("AddRegularPolygon", "{centerXmm:0,centerYmm:0,sides:4,diameterMm:8,angleDegrees:45}", 1840.0)]
        public void SupportedInteriorProfilesCanBeCut(string command, string parameters, double expected)
        {
            var plan = Plan("AddRectangle", new { centerXmm = 0, centerYmm = 0, widthMm = 20, heightMm = 20 });
            Group(plan, command, JObject.Parse(parameters), new { endCondition = "ThroughAll" });
            Assert.AreEqual(expected, CadV2PrismaticGeometry.Analyze(plan).VolumeMm3, 1e-8);
        }

        [DataTestMethod]
        [DataRow(0.0)] [DataRow(5.0)] [DataRow(6.0)] [DataRow(-1.0)] [DataRow(double.NaN)]
        public void BlindCutRequiresPositiveFiniteDepthLessThanBoss(double depth)
        {
            var plan = Plate();
            Group(plan, "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 4 }, new { endCondition = "Blind", depthMm = depth });
            Reject(plan);
        }

        [TestMethod]
        public void RejectsIntersectingTouchingAndNearBoundaryCuts()
        {
            foreach (var x in new[] { 0.0, 4.0 })
            {
                var plan = Plate();
                Group(plan, "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 4 }, new { endCondition = "ThroughAll" });
                Group(plan, "AddCircle", new { centerXmm = x, centerYmm = 0, diameterMm = 4 }, new { endCondition = "ThroughAll" });
                Reject(plan);
            }
            foreach (var x in new[] { 18.0, 17.99, 17.98, 30.0 })
            {
                var plan = Plate();
                Group(plan, "AddCircle", new { centerXmm = x, centerYmm = 0, diameterMm = 4 }, new { endCondition = "ThroughAll" });
                Reject(plan);
            }
        }

        [TestMethod]
        public void RejectsMalformedSequencesReferencesAndUnsupportedScope()
        {
            var mutations = new Action<CadPlanV2Document>[] {
                p => p.Steps[1].Parameters["plane"] = "Front Plane",
                p => p.Steps[4].Inputs.ProfileSketch.OutputKey = "other",
                p => p.Steps[4].Inputs.ProfileSketch.EntityId = Guid.NewGuid(),
                p => p.Steps[4].Inputs.ProfileSketch.Kind = "Face",
                p => p.Steps[1].OutputEntityId = null,
                p => p.Steps[4].OutputKey = "feature",
                p => p.Steps[4].OperationVersion = 1,
                p => p.Steps[2].Parameters["widthMm"] = double.PositiveInfinity,
                p => p.Steps[2].Parameters["widthMm"] = 1e308,
                p => p.Steps[2].Parameters["widthMm"] = 0,
                p => p.Steps.Insert(3, Step("extra", "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 2 })),
                p => p.Steps.Add(Step("extra", "Rebuild", new {})),
                p => p.Steps.Add(Step("save", "SavePart", new { path = "part.sldprt", allowOverwrite = true })),
                p => p.Steps.Add(Step("save", "SavePart", new { path = "part.sldprt" })),
                p => p.Steps.Insert(1, Step("save", "SavePart", new { path = "part.sldprt", allowOverwrite = false })),
                p => p.Steps[2] = Step("line", "AddLine", new { startXmm = 0, startYmm = 0, endXmm = 1, endYmm = 0 }),
                p => p.Steps[0] = null
            };
            foreach (var mutation in mutations) { var plan = Plate(); mutation(plan); Reject(plan); }
            var circle = Plan("AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 40 });
            Group(circle, "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 4 }, new { endCondition = "ThroughAll" });
            Reject(circle);
            Reject(null);
        }

        private static void Reject(CadPlanV2Document plan) => Assert.ThrowsException<ArgumentException>(() => CadV2PrismaticGeometry.Analyze(plan));
        private static CadPlanV2Document Plate() => Plan("AddRectangle", new { centerXmm = 0, centerYmm = 0, widthMm = 40, heightMm = 20 });
        private static CadPlanV2Document Plan(string command, object parameters)
        {
            var plan = new CadPlanV2Document { PlanVersion = 2, Steps = new List<CadPlanV2Step> { Step("new", "NewPart", new {}) } };
            Group(plan, command, parameters, null);
            return plan;
        }
        private static void Group(CadPlanV2Document plan, string primitive, object parameters, object cut)
        {
            int number = (plan.Steps.Count - 1) / 4 + 1;
            var sketch = Step("sketch" + number, "CreateSketch", new { plane = "Top Plane" });
            sketch.OutputKey = "profile" + number;
            sketch.OutputEntityId = Guid.NewGuid();
            plan.Steps.Add(sketch);
            plan.Steps.Add(Step("primitive" + number, primitive, parameters));
            plan.Steps.Add(Step("exit" + number, "ExitSketch", new {}));
            var feature = Step("feature" + number, cut == null ? "Extrude" : "CutExtrude", cut ?? new { depthMm = 5 });
            feature.OperationVersion = 2;
            feature.Inputs = new CadPlanV2Inputs { ProfileSketch = new CadPlanV2Reference { Kind = "Sketch", OutputKey = sketch.OutputKey, EntityId = sketch.OutputEntityId } };
            plan.Steps.Add(feature);
        }
        private static CadPlanV2Step Step(string key, string command, object parameters) => new CadPlanV2Step {
            StepKey = key, Command = command, OperationVersion = 1,
            Parameters = parameters is JObject json ? json : JObject.FromObject(parameters)
        };
    }
}
