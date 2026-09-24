using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentRoutesTests
    {
        private string _databasePath;
        private SqliteJobRepository _repository;
        private AgentRoutes _routes;

        [TestInitialize]
        public async Task Initialize()
        {
            _databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Routes-" + Guid.NewGuid().ToString("N") + ".db");
            _repository = new SqliteJobRepository(_databasePath);
            await _repository.InitializeAsync();
            _routes = new AgentRoutes(_repository, new FakeSolidWorksSession());
        }

        [TestCleanup]
        public void Cleanup()
        {
            _repository?.Dispose();
            TryDelete(_databasePath);
            TryDelete(_databasePath + "-wal");
            TryDelete(_databasePath + "-shm");
        }

        [TestMethod]
        public async Task Health_ReturnsSchemaAndProcessStatus()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/health", null),
                CancellationToken.None);

            Assert.AreEqual(200, response.StatusCode);
            var body = JObject.Parse(response.JsonBody);
            Assert.AreEqual("ok", (string)body["status"]);
            Assert.AreEqual(1, (int)body["schemaVersion"]);
        }

        [TestMethod]
        public async Task CreateJob_PersistsNewJobAndReturnsItsIdentifier()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a plate\"}"),
                CancellationToken.None);

            Assert.AreEqual(201, response.StatusCode);
            var body = JObject.Parse(response.JsonBody);
            var jobId = Guid.Parse((string)body["id"]);
            var saved = await _repository.GetAsync(jobId);
            Assert.AreEqual("Create a plate", saved.Prompt);
            Assert.AreEqual(JobState.New, saved.State);
        }

        [TestMethod]
        public async Task CreateJob_RejectsBlankPromptWithoutWritingAJob()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"   \"}"),
                CancellationToken.None);

            Assert.AreEqual(400, response.StatusCode);
            Assert.AreEqual("PROMPT_REQUIRED", (string)JObject.Parse(response.JsonBody)["error"]["code"]);
        }

        [TestMethod]
        public async Task SolidWorksStatus_ReturnsBridgeStatusWithoutExposingComObjects()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/solidworks/status", null),
                CancellationToken.None);

            Assert.AreEqual(200, response.StatusCode);
            var body = JObject.Parse(response.JsonBody);
            Assert.IsTrue((bool)body["isConnected"]);
            Assert.AreEqual("2020 SP0.0", (string)body["runtime"]["displayVersion"]);
        }

        [TestMethod]
        public async Task UnknownRoute_ReturnsStructuredNotFound()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/not-a-route", null),
                CancellationToken.None);

            Assert.AreEqual(404, response.StatusCode);
            Assert.AreEqual("ROUTE_NOT_FOUND", (string)JObject.Parse(response.JsonBody)["error"]["code"]);
        }

        private sealed class FakeSolidWorksSession : ISolidWorksSession
        {
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(CreateStatus());
            }

            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(CreateStatus());
            }

            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(CreateStatus());
            }

            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }

            private static SolidWorksSessionStatus CreateStatus()
            {
                return new SolidWorksSessionStatus
                {
                    IsConnected = true,
                    IsRunning = true,
                    IsVisible = true,
                    RuntimeInfo = new SolidWorksRuntimeInfo
                    {
                        DisplayVersion = "2020 SP0.0"
                    }
                };
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
