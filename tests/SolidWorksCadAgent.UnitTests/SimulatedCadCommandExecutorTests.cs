using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SimulatedCadCommandExecutorTests
    {
        [TestMethod]
        public async Task AcceptancePlateSequence_ProducesDeterministicInspectableModel()
        {
            ICadCommandExecutor executor = new SimulatedCadCommandExecutor();

            foreach (var command in AcceptancePlateCommands())
            {
                var result = await executor.ExecuteAsync(command, CancellationToken.None);
                Assert.IsTrue(result.Success, result.Error?.Message);
            }

            var bodyCount = await executor.ExecuteAsync(Command(CadCommandNames.GetBodyCount), CancellationToken.None);
            var bounds = await executor.ExecuteAsync(Command(CadCommandNames.GetBoundingBox), CancellationToken.None);
            var features = await executor.ExecuteAsync(Command(CadCommandNames.GetFeatureTree), CancellationToken.None);
            var rebuild = await executor.ExecuteAsync(Command(CadCommandNames.GetRebuildErrors), CancellationToken.None);

            Assert.AreEqual(1, (int)bodyCount.Data["bodyCount"]);
            Assert.AreEqual(100.0, (double)bounds.Data["sizeXmm"], 0.001);
            Assert.AreEqual(60.0, (double)bounds.Data["sizeYmm"], 0.001);
            Assert.AreEqual(10.0, (double)bounds.Data["sizeZmm"], 0.001);
            CollectionAssert.AreEqual(
                new[] { "BossExtrude", "CutExtrude" },
                features.Data["features"].Values<string>().ToArray());
            Assert.IsFalse((bool)rebuild.Data["hasErrors"]);
        }

        [TestMethod]
        public async Task ExecuteAsync_RecordsCommandsInOrder()
        {
            var executor = new SimulatedCadCommandExecutor();

            await executor.ExecuteAsync(Command(CadCommandNames.NewPart), CancellationToken.None);
            await executor.ExecuteAsync(Command(CadCommandNames.Rebuild), CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { CadCommandNames.NewPart, CadCommandNames.Rebuild },
                executor.ExecutedCommands.Select(command => command.Command).ToArray());
        }

        [TestMethod]
        public async Task ExecuteAsync_RejectsUnknownCommand()
        {
            var executor = new SimulatedCadCommandExecutor();

            var result = await executor.ExecuteAsync(Command("RunPowerShell"), CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("UNSUPPORTED_COMMAND", result.Error.Code);
        }

        [TestMethod]
        public async Task SavePart_RejectsNativeSolidWorksExtensionInSimulation()
        {
            var executor = new SimulatedCadCommandExecutor();
            await executor.ExecuteAsync(Command(CadCommandNames.NewPart), CancellationToken.None);

            var result = await executor.ExecuteAsync(
                Command(CadCommandNames.SavePart, new { path = @"C:\Cad\simulated.SLDPRT" }),
                CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("SIMULATION_NATIVE_FORMAT_NOT_ALLOWED", result.Error.Code);
        }

        [TestMethod]
        public async Task SimulatedSession_StatusIsExplicitAndNeverRequiresCom()
        {
            using (var session = new SimulatedSolidWorksSession())
            {
                var status = await session.GetStatusAsync(CancellationToken.None);

                Assert.IsTrue(status.IsConnected);
                Assert.IsTrue(status.IsRunning);
                StringAssert.Contains(status.RuntimeInfo.DisplayVersion, "SIMULATION");
            }
        }

        private static IEnumerable<CadCommandEnvelope> AcceptancePlateCommands()
        {
            yield return Command(CadCommandNames.NewPart);
            yield return Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" });
            yield return Command(CadCommandNames.AddRectangle, new { centreXmm = 0.0, centreYmm = 0.0, widthMm = 100.0, heightMm = 60.0 });
            yield return Command(CadCommandNames.ExitSketch);
            yield return Command(CadCommandNames.Extrude, new { depthMm = 10.0 });
            yield return Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" });
            yield return Command(CadCommandNames.AddCircle, new { centreXmm = 0.0, centreYmm = 0.0, diameterMm = 20.0 });
            yield return Command(CadCommandNames.ExitSketch);
            yield return Command(CadCommandNames.CutExtrude, new { endCondition = "ThroughAll" });
            yield return Command(CadCommandNames.Rebuild);
        }

        private static CadCommandEnvelope Command(string name, object parameters = null)
        {
            return new CadCommandEnvelope
            {
                Command = name,
                Parameters = parameters == null ? new JObject() : JObject.FromObject(parameters)
            };
        }
    }
}
