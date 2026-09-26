using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DeterministicCadPlanningProviderTests
    {
        [TestMethod]
        public async Task PlanAsync_AcceptancePlate_ReturnsValidatedNativeCommandPlan()
        {
            ICadPlanningProvider provider = new DeterministicCadPlanningProvider();

            var result = await provider.PlanAsync(
                new CadPlanningRequest
                {
                    Prompt = "Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole."
                },
                CancellationToken.None);

            Assert.AreEqual("deterministic", result.Provider);
            Assert.AreEqual(0, result.Ambiguities.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    CadCommandNames.NewPart,
                    CadCommandNames.CreateSketch,
                    CadCommandNames.AddRectangle,
                    CadCommandNames.ExitSketch,
                    CadCommandNames.Extrude,
                    CadCommandNames.CreateSketch,
                    CadCommandNames.AddCircle,
                    CadCommandNames.ExitSketch,
                    CadCommandNames.CutExtrude,
                    CadCommandNames.Rebuild
                },
                result.ProposedCommands.Select(command => command.Command).ToArray());
        }

        [TestMethod]
        public async Task PlanAsync_AmbiguousM8Hole_RequiresClarificationAndReturnsNoCommands()
        {
            ICadPlanningProvider provider = new DeterministicCadPlanningProvider();

            var result = await provider.PlanAsync(
                new CadPlanningRequest { Prompt = "Make a plate with an M8 hole" },
                CancellationToken.None);

            Assert.AreEqual(1, result.Ambiguities.Count);
            StringAssert.Contains(result.Ambiguities[0], "tapped or clearance");
            Assert.AreEqual(0, result.ProposedCommands.Count);
        }

        [TestMethod]
        public async Task PlanAsync_M8ClearanceClarification_ProducesResolvedThroughHolePlan()
        {
            ICadPlanningProvider provider = new DeterministicCadPlanningProvider();

            var result = await provider.PlanAsync(
                new CadPlanningRequest
                {
                    Prompt = "Create a 100 x 60 x 10 mm rectangular plate with one centred M8 hole.",
                    Clarifications = new[] { "Use a 9 mm diameter clearance through-hole." }
                },
                CancellationToken.None);

            Assert.AreEqual(0, result.Ambiguities.Count);
            var circle = result.ProposedCommands.Single(command => command.Command == CadCommandNames.AddCircle);
            Assert.AreEqual(9.0, (double)circle.Parameters["diameterMm"]);
            Assert.IsTrue(result.ProposedCommands.Any(command => command.Command == CadCommandNames.CutExtrude));
        }

        [TestMethod]
        public async Task PlanAsync_UnsupportedPrompt_RequestsClarificationInsteadOfGuessing()
        {
            ICadPlanningProvider provider = new DeterministicCadPlanningProvider();

            var result = await provider.PlanAsync(
                new CadPlanningRequest { Prompt = "Make the thing from last week" },
                CancellationToken.None);

            Assert.IsTrue(result.Ambiguities.Count > 0);
            Assert.AreEqual(0, result.ProposedCommands.Count);
        }
    }
}
