using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
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
        public void PrismaticAnalysisExposesOrderedCutReadbackExpectations()
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 });
            Group(plan, "AddCircle", new { centerXmm = -8.0, centerYmm = 12.0, diameterMm = 6.0 },
                new { endCondition = "ThroughAll" });
            Group(plan, "AddSlot", new { centerXmm = 17.0, centerYmm = -1.0, lengthMm = 12.0, widthMm = 4.0, angleDegrees = 23.0 },
                new { endCondition = "Blind", depthMm = 4.0 });

            var cuts = CadV2PrismaticGeometry.Analyze(plan).Cuts;
            Assert.AreEqual(2, cuts.Count);
            Assert.AreEqual("feature2", cuts[0].StepKey);
            Assert.AreEqual("ThroughAll", cuts[0].EndCondition);
            Assert.IsNull(cuts[0].DepthMm);
            Assert.AreEqual(-11.0, cuts[0].MinXmm, 1e-9);
            Assert.AreEqual(-5.0, cuts[0].MaxXmm, 1e-9);
            Assert.AreEqual(0, cuts[0].LineCount);
            Assert.AreEqual(1, cuts[0].ArcCount);
            Assert.AreEqual("Blind", cuts[1].EndCondition);
            Assert.AreEqual(4.0, cuts[1].DepthMm.Value, 1e-9);
            Assert.AreEqual(2, cuts[1].LineCount);
            Assert.AreEqual(2, cuts[1].ArcCount);
            Assert.IsTrue(cuts[1].MinXmm < 12.0 && cuts[1].MaxXmm > 22.0,
                "The rotated offset slot expectation must preserve asymmetric local bounds.");
        }

        [TestMethod]
        public void NativeCutReadbackMustMatchCountParametersDirectionAndParentProfile()
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 });
            Group(plan, "AddCircle", new { centerXmm = -8.0, centerYmm = 12.0, diameterMm = 6.0 },
                new { endCondition = "ThroughAll" });
            Group(plan, "AddRectangle", new { centerXmm = 17.0, centerYmm = -1.0, widthMm = 12.0, heightMm = 8.0 },
                new { endCondition = "Blind", depthMm = 4.0 });
            var expected = CadV2PrismaticGeometry.Analyze(plan).Cuts;
            var observed = new JArray
            {
                NativeCut("ThroughAll", null, -11, -5, 9, 15, 0, 1, new JArray
                {
                    new JObject { ["type"] = "Arc", ["centerXmm"] = -8.0, ["centerYmm"] = 12.0,
                        ["radiusMm"] = 3.0, ["startXmm"] = -5.0, ["startYmm"] = 12.0,
                        ["endXmm"] = -5.0, ["endYmm"] = 12.0, ["rotationDirection"] = -1, ["isCircle"] = true }
                }),
                NativeCut("Blind", 4.0, 11.0, 23.0, -5.0, 3.0, 4, 0, new JArray
                {
                    new JObject { ["type"] = "Line", ["startXmm"] = 11.0, ["startYmm"] = -5.0, ["endXmm"] = 23.0, ["endYmm"] = -5.0 },
                    new JObject { ["type"] = "Line", ["startXmm"] = 23.0, ["startYmm"] = -5.0, ["endXmm"] = 23.0, ["endYmm"] = 3.0 },
                    new JObject { ["type"] = "Line", ["startXmm"] = 23.0, ["startYmm"] = 3.0, ["endXmm"] = 11.0, ["endYmm"] = 3.0 },
                    new JObject { ["type"] = "Line", ["startXmm"] = 11.0, ["startYmm"] = 3.0, ["endXmm"] = 11.0, ["endYmm"] = -5.0 }
                })
            };
            Assert.IsTrue(CadV2NativeCutReadbackMatcher.Matches(expected, observed, out var reason), reason);

            var reorderedAndReversedLines = (JArray)observed.DeepClone();
            var nativeLines = (JArray)reorderedAndReversedLines[1]["profile"]["segments"];
            var firstLine = (JObject)nativeLines[0].DeepClone();
            double startX = firstLine.Value<double>("startXmm"), startY = firstLine.Value<double>("startYmm");
            firstLine["startXmm"] = firstLine["endXmm"];
            firstLine["startYmm"] = firstLine["endYmm"];
            firstLine["endXmm"] = startX;
            firstLine["endYmm"] = startY;
            nativeLines.RemoveAt(0);
            nativeLines.Add(firstLine);
            Assert.IsTrue(CadV2NativeCutReadbackMatcher.Matches(expected, reorderedAndReversedLines, out _),
                "Native segment enumeration order and line endpoint orientation do not change sketch geometry.");

            var changedDepth = (JArray)observed.DeepClone();
            changedDepth[1]["depthMm"] = 4.5;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, changedDepth, out _));
            var shiftedSketch = (JArray)observed.DeepClone();
            shiftedSketch[0]["profile"]["minXmm"] = -10.0;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, shiftedSketch, out _));
            var wrongTopology = (JArray)observed.DeepClone();
            wrongTopology[1]["profile"]["lineCount"] = 3;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, wrongTopology, out _));
            var changedSegmentWithSameBoundsAndTopology = (JArray)observed.DeepClone();
            changedSegmentWithSameBoundsAndTopology[1]["profile"]["segments"][1]["endXmm"] = 9.0;
            changedSegmentWithSameBoundsAndTopology[1]["profile"]["segments"][2]["startXmm"] = 9.0;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, changedSegmentWithSameBoundsAndTopology, out _),
                "A trapezoid with the same line count and overall bounds must not match the planned rectangle.");
            var changedCircleRadius = (JArray)observed.DeepClone();
            changedCircleRadius[0]["profile"]["segments"][0]["radiusMm"] = 2.5;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, changedCircleRadius, out _));
            var wrongDirection = (JArray)observed.DeepClone();
            wrongDirection[0]["reverseDirection"] = false;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, wrongDirection, out _));
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, new JArray(), out _));
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, null, out _));
        }

        [TestMethod]
        public void NativeCutReadbackChecksArcCenterEndpointsAndSweep()
        {
            var plan = ContourCutPlan();
            var expected = CadV2PrismaticGeometry.Analyze(plan).Cuts;
            var observed = new JArray
            {
                NativeCut("ThroughAll", null, -11, -5, 12, 15, 1, 1, new JArray
                {
                    new JObject { ["type"] = "Line", ["startXmm"] = -5.0, ["startYmm"] = 12.0,
                        ["endXmm"] = -11.0, ["endYmm"] = 12.0 },
                    new JObject { ["type"] = "Arc", ["centerXmm"] = -8.0, ["centerYmm"] = 12.0,
                        ["radiusMm"] = 3.0, ["startXmm"] = -11.0, ["startYmm"] = 12.0,
                        ["endXmm"] = -5.0, ["endYmm"] = 12.0, ["rotationDirection"] = -1, ["isCircle"] = false }
                })
            };
            Assert.IsTrue(CadV2NativeCutReadbackMatcher.Matches(expected, observed, out var reason), reason);

            var equivalentReversedArc = (JArray)observed.DeepClone();
            var equivalentArc = equivalentReversedArc[0]["profile"]["segments"][1];
            equivalentArc["startXmm"] = -5.0;
            equivalentArc["endXmm"] = -11.0;
            equivalentArc["rotationDirection"] = 1;
            Assert.IsTrue(CadV2NativeCutReadbackMatcher.Matches(expected, equivalentReversedArc, out _),
                "Reversing arc endpoints and rotation together preserves the same circular sweep.");

            var wrongSweep = (JArray)observed.DeepClone();
            wrongSweep[0]["profile"]["segments"][1]["rotationDirection"] = 1;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, wrongSweep, out _),
                "Changing only the arc sweep must be rejected even when bounds and topology are unchanged.");
            var wrongArcCenter = (JArray)observed.DeepClone();
            wrongArcCenter[0]["profile"]["segments"][1]["centerXmm"] = -7.5;
            Assert.IsFalse(CadV2NativeCutReadbackMatcher.Matches(expected, wrongArcCenter, out _));
        }

        private static JObject NativeCut(string condition, double? depthMm, double minX, double maxX,
            double minY, double maxY, int lines, int arcs, JArray segments) => new JObject
        {
            ["featureName"] = "Cut-Extrude",
            ["endCondition"] = condition,
            ["depthMm"] = depthMm.HasValue ? (JToken)new JValue(depthMm.Value) : JValue.CreateNull(),
            ["reverseDirection"] = true,
            ["flipSideToCut"] = false,
            ["bothDirections"] = false,
            ["profile"] = new JObject
            {
                ["parentRelationVerified"] = true,
                ["sketchName"] = "Sketch",
                ["lineCount"] = lines,
                ["arcCount"] = arcs,
                ["segments"] = segments,
                ["minXmm"] = minX, ["maxXmm"] = maxX,
                ["minYmm"] = minY, ["maxYmm"] = maxY
            }
        };

        private static CadPlanV2Document ContourCutPlan()
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 });
            plan.Steps[4].Parameters["depthMm"] = 13.0;
            var sketch = Step("cut-sketch", "CreateSketch", new { plane = "Top Plane" });
            sketch.OutputKey = "cut-profile"; sketch.OutputEntityId = Guid.NewGuid(); plan.Steps.Add(sketch);
            plan.Steps.Add(Step("cut-line", "AddLine", new { startXmm = -5.0, startYmm = 12.0, endXmm = -11.0, endYmm = 12.0 }));
            plan.Steps.Add(Step("cut-arc", "AddArc", new { centerXmm = -8.0, centerYmm = 12.0,
                startXmm = -11.0, startYmm = 12.0, endXmm = -5.0, endYmm = 12.0, clockwise = true }));
            plan.Steps.Add(Step("cut-exit", "ExitSketch", new { }));
            var cut = Step("cut-feature", "CutExtrude", new { endCondition = "ThroughAll" }); cut.OperationVersion = 2;
            cut.Inputs = new CadPlanV2Inputs { ProfileSketch = new CadPlanV2Reference
            { Kind = "Sketch", OutputKey = sketch.OutputKey, EntityId = sketch.OutputEntityId } };
            plan.Steps.Add(cut);
            return plan;
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

        [DataTestMethod]
        [DataRow("Top Plane", 7.0, 5.0, -11.0)]
        [DataRow("Front Plane", 7.0, 11.0, 5.0)]
        [DataRow("Right Plane", 5.0, 11.0, -7.0)]
        public void StandardPlaneFramesMapAsymmetricSketchCoordinates(string plane, double x, double y, double z)
        {
            var frame = CadStandardPlaneFrame.FromPlaneName(plane);
            var point = frame.ToModel(7, 11, 5);
            Assert.AreEqual(x, point.Xmm, 1e-9, "model X");
            Assert.AreEqual(y, point.Ymm, 1e-9, "model Y");
            Assert.AreEqual(z, point.Zmm, 1e-9, "model Z");
        }

        [DataTestMethod]
        [DataRow("Top Plane", 1.0, 13.0, 0.0, 5.0, -15.0, -7.0)]
        [DataRow("Front Plane", 1.0, 13.0, 7.0, 15.0, 0.0, 5.0)]
        [DataRow("Right Plane", 0.0, 5.0, 7.0, 15.0, -13.0, -1.0)]
        public void AsymmetricBossBoundsFollowStandardPlaneFrame(string plane,
            double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7, centerYmm = 11, widthMm = 12, heightMm = 8 }, plane);
            var geometry = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(12 * 8 * 5, geometry.VolumeMm3, 1e-9);
            Assert.AreEqual(minX, geometry.MinXmm, 1e-9);
            Assert.AreEqual(maxX, geometry.MaxXmm, 1e-9);
            Assert.AreEqual(minY, geometry.MinYmm, 1e-9);
            Assert.AreEqual(maxY, geometry.MaxYmm, 1e-9);
            Assert.AreEqual(minZ, geometry.MinZmm, 1e-9);
            Assert.AreEqual(maxZ, geometry.MaxZmm, 1e-9);
        }

        [DataTestMethod]
        [DataRow("Top Plane", -3.0, 17.0, 0.0, 5.0, -21.0, -11.0)]
        [DataRow("Front Plane", -3.0, 17.0, 11.0, 21.0, 0.0, 5.0)]
        [DataRow("Right Plane", 0.0, 5.0, 11.0, 21.0, -17.0, 3.0)]
        public void OrderedClosedLineArcContourAnalyzesSemicircleAreaAndOffsetBounds(string plane,
            double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            var plan = NewContourPlan(plane, new[]
            {
                Command("AddLine", new { startXmm = 17.0, startYmm = 11.0, endXmm = -3.0, endYmm = 11.0 }),
                Command("AddArc", new { centerXmm = 7.0, centerYmm = 11.0, startXmm = -3.0, startYmm = 11.0, endXmm = 17.0, endYmm = 11.0, clockwise = true })
            });
            var geometry = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(250 * Math.PI, geometry.VolumeMm3, 1e-8);
            Assert.AreEqual(minX, geometry.MinXmm, 1e-9);
            Assert.AreEqual(maxX, geometry.MaxXmm, 1e-9);
            Assert.AreEqual(minY, geometry.MinYmm, 1e-9);
            Assert.AreEqual(maxY, geometry.MaxYmm, 1e-9);
            Assert.AreEqual(minZ, geometry.MinZmm, 1e-9);
            Assert.AreEqual(maxZ, geometry.MaxZmm, 1e-9);
        }

        [TestMethod]
        public void ClosedLineArcContourCanBeAnalyticallyCutFromRectangularBase()
        {
            var plan = Plan("AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 });
            plan.Steps[4].Parameters["depthMm"] = 13.0;
            var sketch = Step("sketch-cut", "CreateSketch", new { plane = "Top Plane" });
            sketch.OutputKey = "profile-cut"; sketch.OutputEntityId = Guid.NewGuid(); plan.Steps.Add(sketch);
            plan.Steps.Add(Step("chord", "AddLine", new { startXmm = -5.0, startYmm = 12.0, endXmm = -11.0, endYmm = 12.0 }));
            plan.Steps.Add(Step("arc", "AddArc", new { centerXmm = -8.0, centerYmm = 12.0,
                startXmm = -11.0, startYmm = 12.0, endXmm = -5.0, endYmm = 12.0, clockwise = true }));
            plan.Steps.Add(Step("exit-cut", "ExitSketch", new { }));
            var cut = Step("cut-contour", "CutExtrude", new { endCondition = "ThroughAll" }); cut.OperationVersion = 2;
            cut.Inputs = new CadPlanV2Inputs { ProfileSketch = new CadPlanV2Reference {
                Kind = "Sketch", OutputKey = sketch.OutputKey, EntityId = sketch.OutputEntityId } };
            plan.Steps.Add(cut);

            var geometry = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(31200 - 58.5 * Math.PI, geometry.VolumeMm3, 1e-8);
            Assert.AreEqual(-23, geometry.MinXmm, 1e-9);
            Assert.AreEqual(37, geometry.MaxXmm, 1e-9);
            Assert.AreEqual(0, geometry.MinYmm, 1e-9);
            Assert.AreEqual(13, geometry.MaxYmm, 1e-9);
            Assert.AreEqual(-31, geometry.MinZmm, 1e-9);
            Assert.AreEqual(9, geometry.MaxZmm, 1e-9);
        }

        [TestMethod]
        public void OrderedClosedLineContourAnalyzesPolygonAreaAndBounds()
        {
            var plan = NewContourPlan("Top Plane", new[]
            {
                Command("AddLine", new { startXmm = 2.0, startYmm = 3.0, endXmm = 12.0, endYmm = 3.0 }),
                Command("AddLine", new { startXmm = 12.0, startYmm = 3.0, endXmm = 2.0, endYmm = 13.0 }),
                Command("AddLine", new { startXmm = 2.0, startYmm = 13.0, endXmm = 2.0, endYmm = 3.0 })
            });
            var geometry = CadV2PrismaticGeometry.Analyze(plan);
            Assert.AreEqual(250, geometry.VolumeMm3, 1e-9);
            Assert.AreEqual(2, geometry.MinXmm, 1e-9);
            Assert.AreEqual(12, geometry.MaxXmm, 1e-9);
            Assert.AreEqual(0, geometry.MinYmm, 1e-9);
            Assert.AreEqual(5, geometry.MaxYmm, 1e-9);
            Assert.AreEqual(-13, geometry.MinZmm, 1e-9);
            Assert.AreEqual(-3, geometry.MaxZmm, 1e-9);
        }

        [DataTestMethod]
        [DataRow("open")]
        [DataRow("disconnected")]
        [DataRow("self-intersecting")]
        [DataRow("mixed")]
        public void InvalidLineArcContoursAreRejectedBeforeAdmission(string kind)
        {
            CadCommandEnvelope[] contour;
            if (kind == "open")
                contour = new[] { Command("AddLine", new { startXmm = 0.0, startYmm = 0.0, endXmm = 10.0, endYmm = 0.0 }),
                    Command("AddLine", new { startXmm = 10.0, startYmm = 0.0, endXmm = 10.0, endYmm = 10.0 }) };
            else if (kind == "disconnected")
                contour = new[] { Command("AddLine", new { startXmm = 0.0, startYmm = 0.0, endXmm = 10.0, endYmm = 0.0 }),
                    Command("AddLine", new { startXmm = 10.0, startYmm = 0.0, endXmm = 10.0, endYmm = 10.0 }),
                    Command("AddLine", new { startXmm = 10.0, startYmm = 10.0, endXmm = 0.0, endYmm = -1.0 }) };
            else if (kind == "mixed")
                contour = new[] { Command("AddLine", new { startXmm = 0.0, startYmm = 0.0, endXmm = 10.0, endYmm = 0.0 }),
                    Command("AddCircle", new { centerXmm = 5.0, centerYmm = 5.0, diameterMm = 2.0 }) };
            else
                contour = new[] { Command("AddLine", new { startXmm = 0.0, startYmm = 0.0, endXmm = 10.0, endYmm = 0.0 }),
                    Command("AddLine", new { startXmm = 10.0, startYmm = 0.0, endXmm = 0.0, endYmm = 10.0 }),
                    Command("AddLine", new { startXmm = 0.0, startYmm = 10.0, endXmm = 10.0, endYmm = 10.0 }),
                    Command("AddLine", new { startXmm = 10.0, startYmm = 10.0, endXmm = 0.0, endYmm = 0.0 }) };
            Assert.ThrowsException<ArgumentException>(() => CadV2PrismaticGeometry.Analyze(NewContourPlan("Top Plane", contour)));
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
                p => p.Steps[1].Parameters["plane"] = "Diagonal Plane",
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

            var mismatchedPlanes = Plate();
            Group(mismatchedPlanes, "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 4 },
                new { endCondition = "ThroughAll" }, "Front Plane");
            Reject(mismatchedPlanes);
        }

        private static void Reject(CadPlanV2Document plan) => Assert.ThrowsException<ArgumentException>(() => CadV2PrismaticGeometry.Analyze(plan));
        private static CadPlanV2Document NewContourPlan(string plane, IEnumerable<CadCommandEnvelope> contour)
        {
            var plan = new CadPlanV2Document { PlanVersion = 2, Steps = new List<CadPlanV2Step> { Step("new", "NewPart", new {}) } };
            var sketch = Step("sketch-contour", "CreateSketch", new { plane });
            sketch.OutputKey = "profile-contour"; sketch.OutputEntityId = Guid.NewGuid(); plan.Steps.Add(sketch);
            int i = 0;
            foreach (var command in contour) plan.Steps.Add(Step("segment-" + (++i), command.Command, command.Parameters));
            plan.Steps.Add(Step("exit-contour", "ExitSketch", new {}));
            var feature = Step("boss-contour", "Extrude", new { depthMm = 5.0 }); feature.OperationVersion = 2;
            feature.Inputs = new CadPlanV2Inputs { ProfileSketch = new CadPlanV2Reference { Kind = "Sketch", OutputKey = sketch.OutputKey, EntityId = sketch.OutputEntityId } };
            plan.Steps.Add(feature);
            return plan;
        }
        private static CadCommandEnvelope Command(string command, object parameters) => new CadCommandEnvelope { Command = command, Parameters = JObject.FromObject(parameters) };
        private static CadPlanV2Document Plate() => Plan("AddRectangle", new { centerXmm = 0, centerYmm = 0, widthMm = 40, heightMm = 20 });
        private static CadPlanV2Document Plan(string command, object parameters, string plane = "Top Plane")
        {
            var plan = new CadPlanV2Document { PlanVersion = 2, Steps = new List<CadPlanV2Step> { Step("new", "NewPart", new {}) } };
            Group(plan, command, parameters, null, plane);
            return plan;
        }
        private static void Group(CadPlanV2Document plan, string primitive, object parameters, object cut, string plane = "Top Plane")
        {
            int number = (plan.Steps.Count - 1) / 4 + 1;
            var sketch = Step("sketch" + number, "CreateSketch", new { plane });
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
