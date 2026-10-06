using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class PrismaticSimulationTests
    {
        [DataTestMethod]
        [DataRow("AddSlot")]
        [DataRow("AddRegularPolygon")]
        public async Task ProfilesRequireRealSolidWorksForGeometryVerification(string name)
        {
            var executor = new SimulatedCadCommandExecutor();
            await Run(executor, "NewPart", new { });
            await Run(executor, "CreateSketch", new { plane = "Top Plane" });
            var parameters = name == "AddSlot"
                ? JObject.FromObject(new { centerXmm = 0, centerYmm = 0, lengthMm = 30, widthMm = 10, angleDegrees = 30 })
                : JObject.FromObject(new { centerXmm = 0, centerYmm = 0, sides = 6, diameterMm = 20, angleDegrees = 30 });
            var profile = await executor.ExecuteAsync(new CadCommandEnvelope { Command = name, Parameters = parameters }, CancellationToken.None);
            Assert.IsTrue(profile.Success, profile.Error?.Message);
            Assert.IsTrue((bool)profile.Data["isSimulated"]);
            await Run(executor, "ExitSketch", new { });
            var extrude = await executor.ExecuteAsync(Command("Extrude", new { depthMm = 10 }), CancellationToken.None);
            Assert.IsFalse(extrude.Success);
            Assert.AreEqual("SIMULATION_PROFILE_UNSUPPORTED", extrude.Error.Code);
        }

        [TestMethod]
        public async Task BlindCutDoesNotPretendToVerifyPocketGeometry()
        {
            var executor = new SimulatedCadCommandExecutor();
            await Run(executor, "NewPart", new { });
            await Run(executor, "CreateSketch", new { plane = "Top Plane" });
            await Run(executor, "AddRectangle", new { centerXmm = 0, centerYmm = 0, widthMm = 100, heightMm = 60 });
            await Run(executor, "ExitSketch", new { });
            await Run(executor, "Extrude", new { depthMm = 10 });
            await Run(executor, "CreateSketch", new { plane = "Top Plane" });
            await Run(executor, "AddCircle", new { centerXmm = 0, centerYmm = 0, diameterMm = 10 });
            await Run(executor, "ExitSketch", new { });
            var result = await executor.ExecuteAsync(Command("CutExtrude", new { endCondition = "Blind", depthMm = 3 }), CancellationToken.None);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("SIMULATION_FEATURE_UNSUPPORTED", result.Error.Code);
        }

        [TestMethod]
        public async Task InvalidSlotRejectedBeforeSketchStateChanges()
        {
            var executor = new SimulatedCadCommandExecutor();
            await Run(executor, "NewPart", new { });
            await Run(executor, "CreateSketch", new { plane = "Top Plane" });
            var result = await executor.ExecuteAsync(Command("AddSlot", new { centerXmm = 0, centerYmm = 0, lengthMm = 5, widthMm = 10, angleDegrees = 0 }), CancellationToken.None);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
            var exit = await executor.ExecuteAsync(Command("ExitSketch", new { }), CancellationToken.None);
            Assert.AreEqual("EMPTY_SKETCH", exit.Error.Code);
        }

        private static CadCommandEnvelope Command(string name, object p) => new CadCommandEnvelope { Command = name, Parameters = JObject.FromObject(p) };
        private static async Task Run(SimulatedCadCommandExecutor executor, string name, object p)
        {
            var result = await executor.ExecuteAsync(Command(name, p), CancellationToken.None);
            Assert.IsTrue(result.Success, result.Error?.Message);
        }
    }
}
