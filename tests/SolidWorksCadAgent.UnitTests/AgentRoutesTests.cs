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
        private FakeSolidWorksSession _solidWorks;

        [TestInitialize]
        public async Task Initialize()
        {
            _databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Routes-" + Guid.NewGuid().ToString("N") + ".db");
            _repository = new SqliteJobRepository(_databasePath);
            await _repository.InitializeAsync();
            _solidWorks = new FakeSolidWorksSession();
            _routes = new AgentRoutes(_repository, _solidWorks);
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

        [TestMethod]
        public async Task AttachAndLaunch_UseTheSolidWorksSessionBoundary()
        {
            var attach = await _routes.HandleAsync(
                new AgentRequest("POST", "/solidworks/attach", null),
                CancellationToken.None);
            var launch = await _routes.HandleAsync(
                new AgentRequest("POST", "/solidworks/launch", null),
                CancellationToken.None);

            Assert.AreEqual(200, attach.StatusCode);
            Assert.AreEqual(200, launch.StatusCode);
            Assert.AreEqual(1, _solidWorks.AttachCalls);
            Assert.AreEqual(1, _solidWorks.LaunchCalls);
        }

        [TestMethod]
        public async Task GetJob_ReturnsPersistedJobAndUnknownIdReturnsNotFound()
        {
            var create = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a bracket\"}"),
                CancellationToken.None);
            var id = (string)JObject.Parse(create.JsonBody)["id"];

            var found = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs/" + id, null),
                CancellationToken.None);
            var missing = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs/" + Guid.NewGuid().ToString("D"), null),
                CancellationToken.None);

            Assert.AreEqual(200, found.StatusCode);
            Assert.AreEqual("Create a bracket", (string)JObject.Parse(found.JsonBody)["prompt"]);
            Assert.IsFalse((bool)JObject.Parse(found.JsonBody)["overwriteRequested"]);
            Assert.IsFalse((bool)JObject.Parse(found.JsonBody)["overwriteAuthorized"]);
            Assert.AreEqual(404, missing.StatusCode);
            Assert.AreEqual("JOB_NOT_FOUND", (string)JObject.Parse(missing.JsonBody)["error"]["code"]);
        }

        [TestMethod]
        public async Task CancelJob_MakesThePersistedJobTerminal()
        {
            var create = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a plate\"}"),
                CancellationToken.None);
            var id = Guid.Parse((string)JObject.Parse(create.JsonBody)["id"]);

            var cancel = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs/" + id.ToString("D") + "/cancel", null),
                CancellationToken.None);

            Assert.AreEqual(200, cancel.StatusCode);
            Assert.AreEqual(JobState.Cancelled, (await _repository.GetAsync(id)).State);
        }

        [TestMethod]
        public async Task ApproveJob_RejectsAJobThatHasNoValidatedPlan()
        {
            var create = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a plate\"}"),
                CancellationToken.None);
            var id = (string)JObject.Parse(create.JsonBody)["id"];

            var approve = await _routes.HandleAsync(
                new AgentRequest("POST", "/jobs/" + id + "/approve", "{}"),
                CancellationToken.None);

            Assert.AreEqual(409, approve.StatusCode);
            Assert.AreEqual("INVALID_JOB_STATE", (string)JObject.Parse(approve.JsonBody)["error"]["code"]);
        }

        private sealed class FakeSolidWorksSession : ISolidWorksSession
        {
            public int AttachCalls { get; private set; }
            public int LaunchCalls { get; private set; }

            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(CreateStatus());
            }

            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
            {
                AttachCalls++;
                return Task.FromResult(CreateStatus());
            }

            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
            {
                LaunchCalls++;
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
