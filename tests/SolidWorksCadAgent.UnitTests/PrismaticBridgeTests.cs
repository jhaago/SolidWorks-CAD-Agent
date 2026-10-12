using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.SolidWorksBridge.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class PrismaticBridgeTests
    {
        private static JObject Slot() => JObject.FromObject(new { centerXmm = 10.0, centerYmm = -5.0, lengthMm = 40.0, widthMm = 12.0, angleDegrees = 25.0 });
        private static JObject Polygon() => JObject.FromObject(new { centerXmm = 10.0, centerYmm = -5.0, sides = 6, diameterMm = 40.0, angleDegrees = -30.0 });
        [DataTestMethod]
        [DataRow("AddSlot")]
        [DataRow("AddRegularPolygon")]
        public async Task ProfileHandlersAreRegisteredAndRejectInvalidGeometryBeforeCom(string command)
        {
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = command, Parameters = new JObject() }, CancellationToken.None);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }
        [DataTestMethod]
        [DataRow("slot", "missingAngle")]
        [DataRow("slot", "stringLength")]
        [DataRow("slot", "equalLengthWidth")]
        [DataRow("slot", "zeroWidth")]
        [DataRow("slot", "infinity")]
        [DataRow("slot", "extra")]
        [DataRow("polygon", "missingAngle")]
        [DataRow("polygon", "fractionalSides")]
        [DataRow("polygon", "tooFewSides")]
        [DataRow("polygon", "tooManySides")]
        [DataRow("polygon", "zeroDiameter")]
        [DataRow("polygon", "infinity")]
        [DataRow("polygon", "extra")]
        [DataRow("polygon", "angleRange")]
        public async Task InvalidProfilesDoNotInvokeSession(string profile, string kind)
        {
            var p = profile == "slot" ? Slot() : Polygon();
            switch (kind)
            {
                case "missingAngle": p.Remove("angleDegrees"); break;
                case "stringLength": p["lengthMm"] = "40"; break;
                case "equalLengthWidth": p["lengthMm"] = 12; break;
                case "zeroWidth": p["widthMm"] = 0; break;
                case "fractionalSides": p["sides"] = 5.5; break;
                case "tooFewSides": p["sides"] = 2; break;
                case "tooManySides": p["sides"] = 33; break;
                case "zeroDiameter": p["diameterMm"] = 0; break;
                case "infinity": p["centerXmm"] = double.PositiveInfinity; break;
                case "angleRange": p["angleDegrees"] = 361; break;
                case "extra": p["arbitraryCom"] = true; break;
            }
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = profile == "slot" ? "AddSlot" : "AddRegularPolygon", Parameters = p }, CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }
        [DataTestMethod]
        [DataRow("missingDepth")]
        [DataRow("zeroDepth")]
        [DataRow("negativeDepth")]
        [DataRow("stringDepth")]
        [DataRow("nanDepth")]
        [DataRow("extra")]
        public async Task InvalidBlindCutsFailBeforeCom(string kind)
        {
            var p = JObject.FromObject(new { endCondition = "Blind", depthMm = 3.0 });
            switch (kind)
            {
                case "missingDepth": p.Remove("depthMm"); break;
                case "zeroDepth": p["depthMm"] = 0; break;
                case "negativeDepth": p["depthMm"] = -1; break;
                case "stringDepth": p["depthMm"] = "3"; break;
                case "nanDepth": p["depthMm"] = double.NaN; break;
                case "extra": p["arbitraryCom"] = true; break;
            }
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = "CutExtrude", Parameters = p }, CancellationToken.None);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }
        [TestMethod]
        public async Task LegacyExtrudeRejectsSketchReferenceInsteadOfUsingCurrentSelection()
        {
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.Extrude,
                    Parameters = JObject.FromObject(new { depthMm = 10.0, sketchEntityId = Guid.NewGuid().ToString("D") })
                }, CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }
        [TestMethod]
        public void ValidProfilesAndBothCutEndConditionsValidateWithoutCom()
        {
            var session = new NeverInvokeSession();
            Assert.IsNull(new AddSlotCommandHandler(session).Validate(Slot()));
            Assert.IsNull(new AddRegularPolygonCommandHandler(session).Validate(Polygon()));
            var cut = new CutExtrudeCommandHandler(session);
            Assert.IsNull(cut.Validate(JObject.FromObject(new { endCondition = "Blind", depthMm = 3.0 })));
            Assert.IsNull(cut.Validate(JObject.FromObject(new { endCondition = "ThroughAll" })));
            Assert.IsNotNull(cut.Validate(JObject.FromObject(new { endCondition = "ThroughAll", depthMm = 3.0 })));
            Assert.AreEqual(0, session.Invocations);
        }
        private sealed class NeverInvokeSession : ISolidWorksSession
        {
            public int Invocations;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> action, CancellationToken token) { Invocations++; throw new InvalidOperationException("Invalid inputs must not invoke SOLIDWORKS."); }
            public void Dispose() { }
        }
    }
}
