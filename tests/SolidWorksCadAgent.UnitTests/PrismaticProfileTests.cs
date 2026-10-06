using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class PrismaticProfileTests
    {
        private static JObject SlotParameters() => JObject.FromObject(new { centerXmm = 10.0, centerYmm = -4.0, lengthMm = 30.0, widthMm = 10.0, angleDegrees = 0.0 });
        private static JObject PolygonParameters() => JObject.FromObject(new { centerXmm = 10.0, centerYmm = -4.0, sides = 6, diameterMm = 20.0, angleDegrees = 0.0 });
        private static CadCommandEnvelope Command(string name, JObject parameters) => new CadCommandEnvelope { Command = name, Parameters = parameters };

        [TestMethod]
        public void ContractSupportsSlotPolygonAndBlindPocket()
        {
            Assert.IsNull(CadPlanningCommandContract.Validate(Command("AddSlot", SlotParameters())));
            Assert.IsNull(CadPlanningCommandContract.Validate(Command("AddRegularPolygon", PolygonParameters())));
            Assert.IsNull(CadPlanningCommandContract.Validate(Command("CutExtrude", JObject.FromObject(new { endCondition = "Blind", depthMm = 5.0 }))));
            StringAssert.Contains(CadPlanningCommandContract.ProtocolDescription, "underside");
        }
        [DataTestMethod]
        [DataRow(0.0)]
        [DataRow(37.0)]
        [DataRow(-90.0)]
        [DataRow(360.0)]
        public void SlotIsClosedCounterclockwiseAndHasAnalyticArea(double angle)
        {
            var p = SlotParameters();
            p["angleDegrees"] = angle;
            var loop = PrismaticProfileGeometry.Slot(p);
            Assert.AreEqual(4, loop.Count);
            Assert.AreEqual(CadCommandNames.AddLine, loop[0].Command);
            Assert.AreEqual(CadCommandNames.AddArc, loop[1].Command);
            Assert.AreEqual(CadCommandNames.AddLine, loop[2].Command);
            Assert.AreEqual(CadCommandNames.AddArc, loop[3].Command);
            AssertLoop(loop);
            Assert.AreEqual(200 + Math.PI * 25, SignedArea(loop), 0.0000001);
        }

        [DataTestMethod]
        [DataRow(3, 0.0)]
        [DataRow(6, 45.0)]
        [DataRow(32, -360.0)]
        public void PolygonUsesCircumcircleAndFirstVertexAngle(int sides, double angle)
        {
            var p = PolygonParameters();
            p["sides"] = sides;
            p["angleDegrees"] = angle;
            var loop = PrismaticProfileGeometry.RegularPolygon(p);
            Assert.AreEqual(sides, loop.Count);
            AssertLoop(loop);
            Assert.AreEqual(10 + 10 * Math.Cos(angle * Math.PI / 180), (double)loop[0].Parameters["startXmm"], 0.0000001);
            Assert.AreEqual(-4 + 10 * Math.Sin(angle * Math.PI / 180), (double)loop[0].Parameters["startYmm"], 0.0000001);
            Assert.AreEqual(sides * 50 * Math.Sin(2 * Math.PI / sides), SignedArea(loop), 0.0000001);
        }

        [DataTestMethod]
        [DataRow("missing")]
        [DataRow("extra")]
        [DataRow("string")]
        [DataRow("nonfinite")]
        [DataRow("angle")]
        [DataRow("negative")]
        [DataRow("degenerate")]
        [DataRow("huge")]
        public void SlotRejectsInvalidAndNumericallyDegenerateGeometry(string kind)
        {
            var p = SlotParameters();
            if (kind == "missing") p.Remove("widthMm");
            if (kind == "extra") p["direction"] = true;
            if (kind == "string") p["centerXmm"] = "10";
            if (kind == "nonfinite") p["widthMm"] = double.NaN;
            if (kind == "angle") p["angleDegrees"] = 360.01;
            if (kind == "negative") p["widthMm"] = -1;
            if (kind == "degenerate") p["lengthMm"] = 10.00000000001;
            if (kind == "huge") p["centerXmm"] = 1e200;
            Assert.IsNotNull(PrismaticProfileGeometry.ValidateSlot(p));
            Assert.ThrowsException<ArgumentException>(() => PrismaticProfileGeometry.Slot(p));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command(CadCommandNames.AddSlot, p)));
        }

        [DataTestMethod]
        [DataRow("sidesLow")]
        [DataRow("sidesHigh")]
        [DataRow("sidesFloat")]
        [DataRow("sidesString")]
        [DataRow("angle")]
        [DataRow("zero")]
        [DataRow("tiny")]
        [DataRow("infinite")]
        [DataRow("extra")]
        public void PolygonRejectsInvalidParameters(string kind)
        {
            var p = PolygonParameters();
            if (kind == "sidesLow") p["sides"] = 2;
            if (kind == "sidesHigh") p["sides"] = 33;
            if (kind == "sidesFloat") p["sides"] = 6.0;
            if (kind == "sidesString") p["sides"] = "6";
            if (kind == "angle") p["angleDegrees"] = -361;
            if (kind == "zero") p["diameterMm"] = 0;
            if (kind == "tiny") p["diameterMm"] = 1e-8;
            if (kind == "infinite") p["diameterMm"] = double.PositiveInfinity;
            if (kind == "extra") p["inscribed"] = true;
            Assert.IsNotNull(PrismaticProfileGeometry.ValidateRegularPolygon(p));
            Assert.ThrowsException<ArgumentException>(() => PrismaticProfileGeometry.RegularPolygon(p));
        }

        [TestMethod]
        public void CutValidationRequiresExplicitPositiveBlindDepthAndRejectsThroughAllDepth()
        {
            Assert.IsNull(PrismaticProfileGeometry.ValidateCut(JObject.FromObject(new { endCondition = "ThroughAll" })));
            foreach (var p in new[]
            {
                JObject.FromObject(new { endCondition = "Blind" }),
                JObject.FromObject(new { endCondition = "Blind", depthMm = 0 }),
                JObject.FromObject(new { endCondition = "Blind", depthMm = -2 }),
                JObject.FromObject(new { endCondition = "Blind", depthMm = "5" }),
                JObject.FromObject(new { endCondition = "Blind", depthMm = double.PositiveInfinity }),
                JObject.FromObject(new { endCondition = "ThroughAll", depthMm = 5 }),
                JObject.FromObject(new { endCondition = "Blind", depthMm = 5, direction = "reverse" }),
                JObject.FromObject(new { endCondition = "UpToSurface" })
            })
            {
                Assert.IsNotNull(PrismaticProfileGeometry.ValidateCut(p));
                Assert.IsNotNull(CadPlanningCommandContract.Validate(Command(CadCommandNames.CutExtrude, p)));
            }
        }

        [TestMethod]
        public void BlindDepthMustRemainPositiveAfterMillimetresToMetresConversion()
        {
            var underflow = JObject.FromObject(new { endCondition = "Blind", depthMm = double.Epsilon });
            Assert.IsNotNull(PrismaticProfileGeometry.ValidateCut(underflow));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(Command(CadCommandNames.CutExtrude, underflow)));
            var representable = JObject.FromObject(new { endCondition = "Blind", depthMm = double.Epsilon * 1000 });
            Assert.IsNull(PrismaticProfileGeometry.ValidateCut(representable));
        }
        private static void AssertLoop(IReadOnlyList<CadCommandEnvelope> loop)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var current = loop[i].Parameters;
                var next = loop[(i + 1) % loop.Count].Parameters;
                Assert.AreEqual((double)current["endXmm"], (double)next["startXmm"], 0.000000001);
                Assert.AreEqual((double)current["endYmm"], (double)next["startYmm"], 0.000000001);
                Assert.IsNull(CadPlanningCommandContract.Validate(loop[i]));
                if (loop[i].Command == CadCommandNames.AddArc) Assert.IsFalse((bool)current["clockwise"]);
            }
        }

        private static double SignedArea(IReadOnlyList<CadCommandEnvelope> loop)
        {
            double integral = 0;
            foreach (var command in loop)
            {
                var p = command.Parameters;
                double sx = (double)p["startXmm"], sy = (double)p["startYmm"], ex = (double)p["endXmm"], ey = (double)p["endYmm"];
                if (command.Command == CadCommandNames.AddLine) integral += sx * ey - ex * sy;
                else
                {
                    double cx = (double)p["centerXmm"], cy = (double)p["centerYmm"];
                    double radiusSquared = (sx - cx) * (sx - cx) + (sy - cy) * (sy - cy);
                    integral += cx * (ey - sy) - cy * (ex - sx) + radiusSquared * Math.PI;
                }
            }
            return integral / 2;
        }    }
}


