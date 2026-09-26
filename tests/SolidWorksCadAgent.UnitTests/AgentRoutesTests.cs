using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Security;
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
        public async Task ListJobs_ReturnsBoundedNewestFirstPageWithCursor()
        {
            var first = await CreateStoredJobAsync("Old", new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc));
            var second = await CreateStoredJobAsync("Middle", new DateTime(2026, 9, 26, 11, 0, 0, DateTimeKind.Utc));
            var third = await CreateStoredJobAsync("Newest", new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc));

            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs?limit=2", null),
                CancellationToken.None);

            Assert.AreEqual(200, response.StatusCode);
            var body = JObject.Parse(response.JsonBody);
            Assert.AreEqual(third, (Guid)body["items"][0]["id"]);
            Assert.AreEqual(second, (Guid)body["items"][1]["id"]);
            Assert.IsFalse(string.IsNullOrWhiteSpace((string)body["nextCursor"]));
            Assert.AreNotEqual(first, (Guid)body["items"][1]["id"]);
        }

        [TestMethod]
        public async Task ListJobs_InvalidCursorReturnsStructuredBadRequest()
        {
            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs?limit=25&cursor=not-a-cursor", null),
                CancellationToken.None);

            Assert.AreEqual(400, response.StatusCode);
            Assert.AreEqual("INVALID_CURSOR", (string)JObject.Parse(response.JsonBody)["error"]["code"]);
        }

        [TestMethod]
        public async Task GetJob_ReturnsCompletePersistedSnapshot()
        {
            var created = new DateTime(2026, 9, 26, 13, 0, 0, DateTimeKind.Utc);
            var jobId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            await _repository.CreateAsync(new CadJob
            {
                Id = jobId,
                Prompt = "Complete snapshot",
                State = JobState.ReadyForReview,
                PlanValidated = true,
                IsSimulated = true,
                OutputPath = @"C:\SolidWorks-CAD-Agent\Workspace\simulation.json",
                CreatedUtc = created,
                UpdatedUtc = created.AddMinutes(4)
            });
            await _repository.AppendRevisionAsync(new JobRevision
            {
                Id = revisionId,
                JobId = jobId,
                RevisionNumber = 1,
                Prompt = "Complete snapshot",
                InterpretationJson = "{\"summary\":\"plate\"}",
                PlanJson = "{\"summary\":\"plan\",\"proposedCommands\":[]}",
                CreatedUtc = created.AddMinutes(1)
            });
            await _repository.AppendCommandAsync(new CommandExecutionRecord
            {
                Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = 1, SequenceNumber = 1,
                CommandName = "NewPart", ParametersJson = "{}", Success = true, ResultJson = "{\"ok\":true}",
                StartedUtc = created.AddMinutes(2), CompletedUtc = created.AddMinutes(2).AddSeconds(1)
            });
            await _repository.AppendVerificationAsync(new VerificationResultRecord
            {
                Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = 1, CheckName = "BodyCount",
                Passed = true, ExpectedJson = "{\"bodyCount\":1}", ActualJson = "{\"bodyCount\":1}",
                CreatedUtc = created.AddMinutes(3)
            });
            await _repository.AddAttachmentAsync(new AttachmentRecord
            {
                Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = 1, Kind = "SimulationReport",
                Path = @"C:\SolidWorks-CAD-Agent\Workspace\simulation.json", CreatedUtc = created.AddMinutes(3)
            });

            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs/" + jobId.ToString("D"), null),
                CancellationToken.None);

            Assert.AreEqual(200, response.StatusCode);
            var body = JObject.Parse(response.JsonBody);
            Assert.AreEqual(revisionId, (Guid)body["currentRevisionId"]);
            Assert.AreEqual(1, body["revisions"].Count());
            Assert.AreEqual(1, body["commands"].Count());
            Assert.AreEqual(1, body["verifications"].Count());
            Assert.AreEqual(1, body["attachments"].Count());
            Assert.IsTrue((bool)body["isSimulated"]);
            Assert.AreEqual(@"C:\SolidWorks-CAD-Agent\Workspace\simulation.json", (string)body["outputPath"]);
            Assert.AreEqual(created, (DateTime)body["createdUtc"]);
            Assert.AreEqual(created.AddMinutes(4), (DateTime)body["updatedUtc"]);
        }

        [TestMethod]
        public async Task GetJob_CorruptPlanReturnsStructuredServerError()
        {
            var created = DateTime.UtcNow;
            var jobId = Guid.NewGuid();
            await _repository.CreateAsync(new CadJob
            {
                Id = jobId, Prompt = "Corrupt snapshot", State = JobState.AwaitingApproval,
                CreatedUtc = created, UpdatedUtc = created
            });
            await _repository.AppendRevisionAsync(new JobRevision
            {
                Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = 1, Prompt = "Corrupt snapshot",
                PlanJson = "{ invalid", CreatedUtc = created
            });

            var response = await _routes.HandleAsync(
                new AgentRequest("GET", "/jobs/" + jobId.ToString("D"), null),
                CancellationToken.None);

            Assert.AreEqual(500, response.StatusCode);
            Assert.AreEqual("CORRUPT_JOB_SNAPSHOT", (string)JObject.Parse(response.JsonBody)["error"]["code"]);
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

        [TestMethod]
        public async Task CoordinatedRoutes_CreatePlansAndApproveExecutesOnlyTheCurrentRevision()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                executor,
                new AgentSettings { AutoMode = false });
            var routes = new AgentRoutes(_repository, _solidWorks, coordinator);

            var create = await routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.\"}"),
                CancellationToken.None);

            Assert.AreEqual(201, create.StatusCode);
            var created = JObject.Parse(create.JsonBody);
            Assert.AreEqual("AwaitingApproval", (string)created["state"]);
            var jobId = Guid.Parse((string)created["id"]);
            var revisionId = Guid.Parse((string)created["currentRevisionId"]);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);

            var approve = await routes.HandleAsync(
                new AgentRequest(
                    "POST",
                    "/jobs/" + jobId.ToString("D") + "/approve",
                    "{\"revisionId\":\"" + revisionId.ToString("D") + "\"}"),
                CancellationToken.None);

            Assert.AreEqual(200, approve.StatusCode);
            Assert.AreEqual("ReadyForReview", (string)JObject.Parse(approve.JsonBody)["state"]);
            Assert.IsTrue(executor.ExecutedCommands.Count > 0);
        }

        [TestMethod]
        public async Task CoordinatedApprove_RejectsMissingRevisionWithoutExecutingCad()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                executor,
                new AgentSettings { AutoMode = false });
            var routes = new AgentRoutes(_repository, _solidWorks, coordinator);
            var create = await routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.\"}"),
                CancellationToken.None);
            var jobId = (string)JObject.Parse(create.JsonBody)["id"];

            var approve = await routes.HandleAsync(
                new AgentRequest("POST", "/jobs/" + jobId + "/approve", "{}"),
                CancellationToken.None);

            Assert.AreEqual(400, approve.StatusCode);
            Assert.AreEqual("REVISION_REQUIRED", (string)JObject.Parse(approve.JsonBody)["error"]["code"]);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task RequestChanges_AppendsRevisionAndInvalidatesOldApproval()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                executor,
                new AgentSettings { AutoMode = false });
            var routes = new AgentRoutes(_repository, _solidWorks, coordinator);
            var create = await routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred M8 hole.\"}"),
                CancellationToken.None);
            var created = JObject.Parse(create.JsonBody);
            var jobId = (Guid)created["id"];
            var originalRevisionId = (Guid)created["currentRevisionId"];
            Assert.AreEqual("AwaitingClarification", (string)created["state"]);

            var changed = await routes.HandleAsync(
                new AgentRequest(
                    "POST",
                    "/jobs/" + jobId.ToString("D") + "/request-changes",
                    "{\"revisionId\":\"" + originalRevisionId.ToString("D") + "\",\"instructions\":\"Use a 9 mm diameter clearance through-hole.\"}"),
                CancellationToken.None);

            Assert.AreEqual(200, changed.StatusCode);
            var body = JObject.Parse(changed.JsonBody);
            Assert.AreEqual("AwaitingApproval", (string)body["state"]);
            Assert.AreEqual(2, (int)body["currentRevisionNumber"]);
            Assert.AreEqual(2, body["revisions"].Count());
            Assert.AreEqual("Use a 9 mm diameter clearance through-hole.", (string)body["revisions"][1]["prompt"]);

            var staleApproval = await routes.HandleAsync(
                new AgentRequest(
                    "POST",
                    "/jobs/" + jobId.ToString("D") + "/approve",
                    "{\"revisionId\":\"" + originalRevisionId.ToString("D") + "\"}"),
                CancellationToken.None);
            Assert.AreEqual(409, staleApproval.StatusCode);
            Assert.AreEqual("STALE_PLAN", (string)JObject.Parse(staleApproval.JsonBody)["error"]["code"]);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task RequestChanges_RejectsBlankInstructionsAndStaleOrConcurrentRevision()
        {
            var coordinator = new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                new SimulatedCadCommandExecutor(),
                new AgentSettings { AutoMode = false });
            var routes = new AgentRoutes(_repository, _solidWorks, coordinator);
            var create = JObject.Parse((await routes.HandleAsync(
                new AgentRequest("POST", "/jobs", "{\"prompt\":\"Create a 100 x 60 x 10 mm rectangular plate with one centred M8 hole.\"}"),
                CancellationToken.None)).JsonBody);
            var jobId = (Guid)create["id"];
            var revisionId = (Guid)create["currentRevisionId"];

            var blank = await routes.HandleAsync(
                new AgentRequest("POST", "/jobs/" + jobId.ToString("D") + "/request-changes",
                    "{\"revisionId\":\"" + revisionId.ToString("D") + "\",\"instructions\":\"   \"}"),
                CancellationToken.None);
            Assert.AreEqual(400, blank.StatusCode);
            Assert.AreEqual("INSTRUCTIONS_REQUIRED", (string)JObject.Parse(blank.JsonBody)["error"]["code"]);

            var requestBody = "{\"revisionId\":\"" + revisionId.ToString("D") + "\",\"instructions\":\"Use a 9 mm diameter clearance through-hole.\"}";
            var requests = new[]
            {
                routes.HandleAsync(new AgentRequest("POST", "/jobs/" + jobId.ToString("D") + "/request-changes", requestBody), CancellationToken.None),
                routes.HandleAsync(new AgentRequest("POST", "/jobs/" + jobId.ToString("D") + "/request-changes", requestBody), CancellationToken.None)
            };
            var responses = await Task.WhenAll(requests);

            CollectionAssert.AreEquivalent(new[] { 200, 409 }, responses.Select(item => item.StatusCode).ToArray());
            Assert.AreEqual(2, (await _repository.GetSnapshotAsync(jobId)).Revisions.Count);
            var conflict = responses.Single(item => item.StatusCode == 409);
            Assert.IsTrue(new[] { "STALE_PLAN", "CONCURRENT_JOB_UPDATE" }
                .Contains((string)JObject.Parse(conflict.JsonBody)["error"]["code"]));
        }

        [TestMethod]
        public async Task Settings_RoundTripPersistsValidatedValuesAndRejectsModeChangeWithActiveJob()
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-SettingsRoutes-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var settings = new AgentSettings();
                var store = new JsonAgentSettingsStore(settingsPath);
                var service = new AgentSettingsService(settings, store, new FakeSecretStore(), _repository);
                var routes = new AgentRoutes(_repository, _solidWorks, null, service);

                var update = await routes.HandleAsync(
                    new AgentRequest("PUT", "/settings",
                        "{\"workspaceRoot\":\"C:\\\\CadWorkspace\",\"autoMode\":true,\"openAiModel\":\"gpt-test\",\"solidWorksExecutablePath\":\"C:\\\\Program Files\\\\SOLIDWORKS.exe\",\"loggingLevel\":\"Debug\",\"executionMode\":\"Simulation\"}"),
                    CancellationToken.None);

                Assert.AreEqual(200, update.StatusCode);
                var updated = JObject.Parse(update.JsonBody);
                Assert.IsTrue((bool)updated["restartRequired"]);
                Assert.AreEqual("Simulation", (string)updated["settings"]["executionMode"]);
                Assert.IsTrue(settings.AutoMode);
                Assert.AreEqual(ExecutionMode.Simulation, store.Load().ExecutionMode);

                await _repository.CreateAsync(new CadJob
                {
                    Id = Guid.NewGuid(), Prompt = "Active", State = JobState.AwaitingApproval,
                    CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
                });
                var blocked = await routes.HandleAsync(
                    new AgentRequest("PUT", "/settings",
                        "{\"workspaceRoot\":\"C:\\\\CadWorkspace\",\"autoMode\":true,\"openAiModel\":\"gpt-test\",\"loggingLevel\":\"Debug\",\"executionMode\":\"Real\"}"),
                    CancellationToken.None);
                Assert.AreEqual(409, blocked.StatusCode);
                Assert.AreEqual("ACTIVE_JOBS_BLOCK_MODE_CHANGE", (string)JObject.Parse(blocked.JsonBody)["error"]["code"]);
                Assert.AreEqual(ExecutionMode.Simulation, settings.ExecutionMode);
            }
            finally
            {
                TryDelete(settingsPath);
                TryDelete(settingsPath + ".bak");
                TryDelete(settingsPath + ".tmp");
            }
        }

        [TestMethod]
        public async Task Settings_InvalidUpdateIsAtomicAndCredentialRoutesNeverReadBackSecret()
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-CredentialRoutes-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var settings = new AgentSettings();
                var secrets = new FakeSecretStore();
                var service = new AgentSettingsService(settings, new JsonAgentSettingsStore(settingsPath), secrets, _repository);
                var routes = new AgentRoutes(_repository, _solidWorks, null, service);

                var invalid = await routes.HandleAsync(
                    new AgentRequest("PUT", "/settings",
                        "{\"workspaceRoot\":\"relative\",\"autoMode\":true,\"openAiModel\":\"gpt-test\",\"loggingLevel\":\"Debug\",\"executionMode\":\"Real\"}"),
                    CancellationToken.None);
                Assert.AreEqual(400, invalid.StatusCode);
                Assert.IsFalse(settings.AutoMode);
                Assert.AreEqual(@"C:\SolidWorks-CAD-Agent\Workspace", settings.WorkspaceRoot);

                const string apiKey = "sk-test-secret-value";
                var set = await routes.HandleAsync(
                    new AgentRequest("PUT", "/credentials/openai", "{\"apiKey\":\"" + apiKey + "\"}"),
                    CancellationToken.None);
                var status = await routes.HandleAsync(
                    new AgentRequest("GET", "/credentials/openai", null),
                    CancellationToken.None);
                var deleted = await routes.HandleAsync(
                    new AgentRequest("DELETE", "/credentials/openai", null),
                    CancellationToken.None);

                Assert.AreEqual(200, set.StatusCode);
                Assert.AreEqual(200, status.StatusCode);
                Assert.IsTrue((bool)JObject.Parse(status.JsonBody)["configured"]);
                Assert.IsFalse(set.JsonBody.Contains(apiKey));
                Assert.IsFalse(status.JsonBody.Contains(apiKey));
                Assert.AreEqual(0, secrets.GetCalls);
                Assert.AreEqual(200, deleted.StatusCode);
                Assert.IsFalse((bool)JObject.Parse(deleted.JsonBody)["configured"]);
            }
            finally
            {
                TryDelete(settingsPath);
                TryDelete(settingsPath + ".bak");
                TryDelete(settingsPath + ".tmp");
            }
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

        private sealed class FakeSecretStore : ISecretStore
        {
            private string _secret;
            public int GetCalls { get; private set; }
            public void Set(string target, string secret) { _secret = secret; }
            public string Get(string target) { GetCalls++; return _secret; }
            public bool Exists(string target) { return _secret != null; }
            public void Delete(string target) { _secret = null; }
        }

        private async Task<Guid> CreateStoredJobAsync(string prompt, DateTime updatedUtc)
        {
            var id = Guid.NewGuid();
            await _repository.CreateAsync(new CadJob
            {
                Id = id,
                Prompt = prompt,
                State = JobState.New,
                CreatedUtc = updatedUtc,
                UpdatedUtc = updatedUtc
            });
            return id;
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
