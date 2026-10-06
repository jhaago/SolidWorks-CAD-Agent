using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.AgentHost.Simulation;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AdvancedSketchTests
    {
        public static JObject Line() => JObject.FromObject(new { startXmm = 0.0, startYmm = 0.0, endXmm = 30.0, endYmm = 10.0 });
        public static JObject Arc() => JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, startXmm = 10.0, startYmm = 0.0, endXmm = 0.0, endYmm = 10.0, clockwise = false });
        private static CadCommandEnvelope Command(string name, JObject p) => new CadCommandEnvelope { Command = name, Parameters = p };

        [TestMethod]
        public void Planner_AcceptsExplicitLineAndArcProtocols()
        {
            Assert.IsNull(CadPlanningCommandContract.Validate(Command("AddLine", Line())));
            Assert.IsNull(CadPlanningCommandContract.Validate(Command("AddArc", Arc())));
            StringAssert.Contains(CadPlanningCommandContract.ProtocolDescription, "AddArc");
        }

        [DataTestMethod]
        [DataRow("missing")]
        [DataRow("string")]
        [DataRow("nan")]
        [DataRow("zero")]
        [DataRow("extra")]
        public async Task Line_InvalidGeometryFailsBeforeCom(string kind)
        {
            var p = Line();
            if (kind == "missing") p.Remove("startXmm");
            if (kind == "string") p["startXmm"] = "0";
            if (kind == "nan") p["startXmm"] = double.NaN;
            if (kind == "zero") { p["endXmm"] = 0; p["endYmm"] = 0; }
            if (kind == "extra") p["arbitrary"] = true;
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(Command("AddLine", p), CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
                Assert.IsNotNull(CadPlanningCommandContract.Validate(Command("AddLine", p)));
            }
        }

        [DataTestMethod]
        [DataRow("missingDirection")]
        [DataRow("stringDirection")]
        [DataRow("centerStart")]
        [DataRow("radii")]
        [DataRow("sameEnd")]
        [DataRow("infinity")]
        public async Task Arc_InvalidGeometryFailsBeforeCom(string kind)
        {
            var p = Arc();
            if (kind == "missingDirection") p.Remove("clockwise");
            if (kind == "stringDirection") p["clockwise"] = "false";
            if (kind == "centerStart") p["startXmm"] = 0;
            if (kind == "radii") p["endYmm"] = 12;
            if (kind == "sameEnd") { p["endXmm"] = 10; p["endYmm"] = 0; }
            if (kind == "infinity") p["centerXmm"] = double.PositiveInfinity;
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(Command("AddArc", p), CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }

        [TestMethod]
        public async Task Simulation_CustomProfilesFailExplicitlyRatherThanFabricatingSolids()
        {
            var sim = new SimulatedCadCommandExecutor();
            await sim.ExecuteAsync(Command("NewPart", new JObject()), CancellationToken.None);
            var noSketch = await sim.ExecuteAsync(Command("AddLine", Line()), CancellationToken.None);
            Assert.AreEqual("NO_ACTIVE_SKETCH", noSketch.Error.Code);
            await sim.ExecuteAsync(Command("CreateSketch", JObject.FromObject(new { plane = "Top Plane" })), CancellationToken.None);
            Assert.IsTrue((await sim.ExecuteAsync(Command("AddLine", Line()), CancellationToken.None)).Success);
            Assert.IsTrue((await sim.ExecuteAsync(Command("AddArc", Arc()), CancellationToken.None)).Success);
            await sim.ExecuteAsync(Command("ExitSketch", new JObject()), CancellationToken.None);
            var extrude = await sim.ExecuteAsync(Command("Extrude", JObject.FromObject(new { depthMm = 5 })), CancellationToken.None);
            Assert.IsFalse(extrude.Success);
            Assert.AreEqual("SIMULATION_PROFILE_UNSUPPORTED", extrude.Error.Code);
        }

        private sealed class NeverInvokeSession : ISolidWorksSession
        {
            public int Invocations;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> action, CancellationToken token) { Invocations++; throw new InvalidOperationException("COM must not run for invalid inputs."); }
            public void Dispose() { }
        }
    }
}