using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class PlannerFailureDiagnosticsTests
    {
        [TestMethod]
        public async Task CreateJob_OpenAiPlanningFailure_ReturnsStructuredDiagnostic()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "SolidWorksCadAgent-PlannerDiagnostics-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                using (var solidWorks = new SimulatedSolidWorksSession())
                {
                    await repository.InitializeAsync();
                    var coordinator = new JobCoordinator(
                        repository,
                        new ThrowingPlanningProvider(new OpenAiPlanningException(
                            "OPENAI_HTTP_ERROR",
                            "OpenAI returned HTTP 400 while planning the CAD job.")),
                        new SimulatedCadCommandExecutor(),
                        new AgentSettings { AutoMode = false, ExecutionMode = ExecutionMode.Real });
                    var routes = new AgentRoutes(repository, solidWorks, coordinator);

                    AgentResponse response;
                    try
                    {
                        response = await routes.HandleAsync(
                            new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a plate\"}"),
                            CancellationToken.None);
                    }
                    catch (OpenAiPlanningException)
                    {
                        Assert.Fail("OpenAI planning failures must not escape the Agent Host route as an unstructured exception.");
                        return;
                    }

                    Assert.AreEqual(502, response.StatusCode);
                    var body = JObject.Parse(response.JsonBody);
                    Assert.AreEqual("OPENAI_HTTP_ERROR", (string)body["error"]["code"]);
                    Assert.AreEqual(
                        "OpenAI returned HTTP 400 while planning the CAD job.",
                        (string)body["error"]["message"]);
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        private sealed class ThrowingPlanningProvider : ICadPlanningProvider
        {
            private readonly OpenAiPlanningException _exception;

            public ThrowingPlanningProvider(OpenAiPlanningException exception)
            {
                _exception = exception;
            }

            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken cancellationToken)
            {
                throw _exception;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
