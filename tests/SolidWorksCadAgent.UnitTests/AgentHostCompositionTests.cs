using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Security;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentHostCompositionTests
    {
        [TestMethod]
        public async Task CreateRoutes_ComposesPlannerCoordinatorAndExecutor()
        {
            var path = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Composition-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var repository = new SqliteJobRepository(path))
                using (var session = new FakeSolidWorksSession())
                {
                    await repository.InitializeAsync();
                    var routes = AgentHostComposition.CreateRoutes(
                        repository,
                        session,
                        new DeterministicCadPlanningProvider(),
                        new SimulatedCadCommandExecutor(),
                        new AgentSettings { AutoMode = false });

                    var response = await routes.HandleAsync(
                        new AgentRequest(
                            "POST",
                            "/jobs",
                            "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.\"}"),
                        CancellationToken.None);

                    Assert.AreEqual(201, response.StatusCode);
                    Assert.AreEqual("AwaitingApproval", (string)JObject.Parse(response.JsonBody)["state"]);
                }
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        [TestMethod]
        public async Task SimulationComposition_NeverInvokesRealFactoriesAndLabelsHealthAndJobs()
        {
            var path = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-SimulationComposition-" + Guid.NewGuid().ToString("N") + ".db");
            var settingsPath = path + ".settings.json";
            try
            {
                using (var repository = new SqliteJobRepository(path))
                {
                    await repository.InitializeAsync();
                    var settings = new AgentSettings { AutoMode = false, ExecutionMode = ExecutionMode.Simulation };
                    var realFactoryCalls = 0;
                    Func<ISolidWorksSession> sessionFactory = () => { realFactoryCalls++; throw new AssertFailedException("Real session factory was invoked."); };
                    Func<ICadPlanningProvider> plannerFactory = () => { realFactoryCalls++; throw new AssertFailedException("Real planner factory was invoked."); };
                    Func<ISolidWorksSession, ICadCommandExecutor> executorFactory = session => { realFactoryCalls++; throw new AssertFailedException("Real executor factory was invoked."); };

                    var routes = AgentHostComposition.CreateRoutesForMode(
                        repository,
                        settings,
                        new JsonAgentSettingsStore(settingsPath),
                        new FakeSecretStore(),
                        sessionFactory,
                        plannerFactory,
                        executorFactory);

                    var health = JObject.Parse((await routes.HandleAsync(
                        new AgentRequest("GET", "/health", null), CancellationToken.None)).JsonBody);
                    Assert.AreEqual("Simulation", (string)health["executionMode"]);
                    Assert.AreEqual(BridgeBuildCapabilities.Capability, (string)health["bridgeCapability"]);

                    var job = JObject.Parse((await routes.HandleAsync(
                        new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.\"}"),
                        CancellationToken.None)).JsonBody);
                    Assert.IsTrue((bool)job["isSimulated"]);
                    Assert.AreEqual(0, realFactoryCalls);
                }
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
                TryDelete(settingsPath);
            }
        }

        private sealed class FakeSolidWorksSession : ISolidWorksSession
        {
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken) =>
                Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
            public void Dispose() { }
        }

        private sealed class FakeSecretStore : ISecretStore
        {
            public void Set(string target, string secret) { }
            public string Get(string target) => null;
            public bool Exists(string target) => false;
            public void Delete(string target) { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
