using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ManagedModelIdentityTests
    {
        private const string LegacyPlanJson = "{\"ProposedCommands\":[{\"Command\":\"CreateSketch\",\"Parameters\":{\"plane\":\"Top Plane\"}}]}";

        [TestMethod]
        public void LegacyPlanJson_ExcludesExecutionIdentityAndStillDeserializes()
        {
            var command = new CadCommandEnvelope
            {
                Command = "CreateSketch",
                Parameters = JObject.Parse("{\"plane\":\"Top Plane\"}"),
                ExecutionId = Guid.NewGuid(),
                ManagedModelId = Guid.NewGuid(),
                OutputEntityId = Guid.NewGuid()
            };

            var serialized = JsonConvert.SerializeObject(command);
            Assert.IsFalse(serialized.Contains("ExecutionId"));
            Assert.IsFalse(serialized.Contains("ManagedModelId"));
            Assert.IsFalse(serialized.Contains("OutputEntityId"));
            var historicPlan = JsonConvert.DeserializeObject<CadPlanningResult>(LegacyPlanJson);
            Assert.AreEqual("CreateSketch", historicPlan.ProposedCommands.Single().Command);
            Assert.AreEqual("Top Plane", (string)historicPlan.ProposedCommands.Single().Parameters["plane"]);
            Assert.IsNull(historicPlan.ProposedCommands.Single().ManagedModelId);
            Assert.IsNull(historicPlan.ProposedCommands.Single().OutputEntityId);
        }

        [TestMethod]
        public async Task ApprovedRealPlan_PersistsIdentityBeforeDispatchAndSavesModelCheckpoint()
        {
            var databasePath = TempDatabasePath();
            var workspace = Path.Combine(Path.GetTempPath(), "SolidWorks-ManagedIdentity-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var executor = new IdentityProbeExecutor(repository, workspace);
                    var coordinator = new JobCoordinator(repository, new FixedProvider(CompletePartPlan(true)), executor,
                        new AgentSettings { WorkspaceRoot = workspace, AutoMode = false, ExecutionMode = ExecutionMode.Real });

                    var planned = await coordinator.CreateAndPlanAsync("Create and save a managed test part", CancellationToken.None);
                    Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
                    var complete = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);

                    Assert.AreEqual(JobState.ReadyForReview, complete.Job.State);
                    Assert.IsTrue(executor.PendingModelObservedBeforeNewPart, "A pending Host identity must exist before NewPart is dispatched.");
                    Assert.IsTrue(executor.PendingBindingObservedBeforeCreateSketch, "A pending logical sketch ID must be stored before CreateSketch is dispatched.");
                    var newPart = executor.Received.Single(command => command.Command == CadCommandNames.NewPart);
                    var createSketch = executor.Received.Single(command => command.Command == CadCommandNames.CreateSketch);
                    Assert.IsTrue(newPart.ManagedModelId.HasValue);
                    Assert.AreEqual(newPart.ManagedModelId, createSketch.ManagedModelId);
                    Assert.IsTrue(createSketch.OutputEntityId.HasValue);

                    var model = await repository.GetModelAsync(newPart.ManagedModelId.Value, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.ActiveSaved, model.Status);
                    Assert.AreEqual("Default", model.ConfigurationKey);
                    Assert.AreEqual(Path.GetFullPath(Path.Combine(workspace, "managed-identity.sldprt")), model.CanonicalPath);
                    Assert.AreEqual("acb86a9cb70a84f695de89e7fe22819466205759d798d52d4a3dd95b0cdaa2a1", model.LastSavedSha256);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(model.CurrentModelRevisionId.ToString("D")));
                    var binding = await repository.GetEntityBindingAsync(model.ModelId, createSketch.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.IsNotNull(binding);
                    Assert.AreEqual(CadEntityReferenceStatus.Pending, binding.Status);
                    Assert.IsNull(binding.NativeReferenceBytes);
                    Assert.IsFalse(JObject.Parse(planned.Revisions.Single().PlanJson).ToString(Formatting.None).Contains("ManagedModelId"));
                }
            }
            finally
            {
                TryDeleteDatabase(databasePath);
                if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            }
        }

        [TestMethod]
        public async Task SimulationPlan_DoesNotCreateOrDispatchManagedIdentity()
        {
            var databasePath = TempDatabasePath();
            var workspace = Path.Combine(Path.GetTempPath(), "SolidWorks-SimIdentity-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var executor = new IdentityProbeExecutor(repository, workspace);
                    var coordinator = new JobCoordinator(repository, new FixedProvider(CompletePartPlan(false)), executor,
                        new AgentSettings { WorkspaceRoot = workspace, AutoMode = false, ExecutionMode = ExecutionMode.Simulation });

                    var planned = await coordinator.CreateAndPlanAsync("Simulate a test part", CancellationToken.None);
                    var complete = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);

                    Assert.AreEqual(JobState.ReadyForReview, complete.Job.State);
                    Assert.IsTrue(executor.Received.Count > 0);
                    Assert.IsTrue(executor.Received.All(command => !command.ManagedModelId.HasValue && !command.OutputEntityId.HasValue));
                    Assert.AreEqual(0L, CountRows(databasePath, "ManagedModels"));
                    Assert.AreEqual(0L, CountRows(databasePath, "EntityReferenceBindings"));
                }
            }
            finally { TryDeleteDatabase(databasePath); }
        }

        [TestMethod]
        public async Task FailedManagedSketchCreation_MarksModelAndPendingReferenceUncertainWithoutRetry()
        {
            var databasePath = TempDatabasePath();
            var workspace = Path.Combine(Path.GetTempPath(), "SolidWorks-ManagedIdentityFailure-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var executor = new IdentityProbeExecutor(repository, workspace, CadCommandNames.CreateSketch);
                    var coordinator = new JobCoordinator(repository, new FixedProvider(CompletePartPlan(false)), executor,
                        new AgentSettings { WorkspaceRoot = workspace, AutoMode = false, ExecutionMode = ExecutionMode.Real });

                    var planned = await coordinator.CreateAndPlanAsync("Create a managed test part", CancellationToken.None);
                    var failed = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);

                    Assert.AreEqual(JobState.Failed, failed.Job.State);
                    var sketch = executor.Received.Single(command => command.Command == CadCommandNames.CreateSketch);
                    var model = await repository.GetModelAsync(sketch.ManagedModelId.Value, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                    var binding = await repository.GetEntityBindingAsync(model.ModelId, sketch.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Uncertain, binding.Status);
                    Assert.IsNull(binding.NativeReferenceBytes);
                    Assert.AreEqual(1, executor.Received.Count(command => command.Command == CadCommandNames.CreateSketch), "The Host must not retry a possibly mutating native command.");
                }
            }
            finally { TryDeleteDatabase(databasePath); }
        }

        private static CadPlanningResult CompletePartPlan(bool save) => new CadPlanningResult
        {
            Summary = "Create a supported rectangular part.",
            ProposedCommands = new List<CadCommandEnvelope>
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 5.0 })
            }.Concat(save ? new[] { Command(CadCommandNames.SavePart, new { path = "managed-identity.sldprt" }) } : Array.Empty<CadCommandEnvelope>()).ToList()
        };

        private static CadCommandEnvelope Command(string name, object parameters = null) => new CadCommandEnvelope
        {
            Command = name,
            Parameters = parameters == null ? new JObject() : JObject.FromObject(parameters)
        };

        private static string TempDatabasePath() => Path.Combine(Path.GetTempPath(), "SolidWorks-ManagedIdentity-" + Guid.NewGuid().ToString("N") + ".db");

        private static long CountRows(string databasePath, string table)
        {
            using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + databasePath + ";Version=3;"))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static void TryDeleteDatabase(string path)
        {
            foreach (var item in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(item)) File.Delete(item);
        }

        private sealed class FixedProvider : ICadPlanningProvider
        {
            private readonly CadPlanningResult _plan;
            public FixedProvider(CadPlanningResult plan) { _plan = plan; }
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token) => Task.FromResult(_plan);
        }

        private sealed class IdentityProbeExecutor : ICadCommandExecutor
        {
            private readonly SqliteJobRepository _repository;
            private readonly string _workspace;
            private readonly string _failureCommand;
            public readonly List<CadCommandEnvelope> Received = new List<CadCommandEnvelope>();
            public bool PendingModelObservedBeforeNewPart { get; private set; }
            public bool PendingBindingObservedBeforeCreateSketch { get; private set; }

            public IdentityProbeExecutor(SqliteJobRepository repository, string workspace, string failureCommand = null)
            {
                _repository = repository;
                _workspace = workspace;
                _failureCommand = failureCommand;
            }

            public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            {
                Received.Add(new CadCommandEnvelope
                {
                    Command = command.Command,
                    Parameters = command.Parameters == null ? new JObject() : (JObject)command.Parameters.DeepClone(),
                    ExecutionId = command.ExecutionId,
                    ManagedModelId = command.ManagedModelId,
                    OutputEntityId = command.OutputEntityId
                });
                if (command.Command == CadCommandNames.NewPart && command.ManagedModelId.HasValue)
                {
                    var model = await _repository.GetModelAsync(command.ManagedModelId.Value, token);
                    PendingModelObservedBeforeNewPart = model != null && model.Status == CadModelIdentityStatus.Pending;
                    return CadCommandResult.Ok(new { documentTitle = "Part1", configurationKey = "Default" });
                }
                if (command.Command == CadCommandNames.CreateSketch && command.ManagedModelId.HasValue && command.OutputEntityId.HasValue)
                {
                    var binding = await _repository.GetEntityBindingAsync(command.ManagedModelId.Value, command.OutputEntityId.Value, "Default", token);
                    PendingBindingObservedBeforeCreateSketch = binding != null && binding.Status == CadEntityReferenceStatus.Pending && binding.NativeReferenceBytes == null;
                }
                if (command.Command == _failureCommand)
                    return new CadCommandResult { Success = false, Data = new JObject(), Error = new CadError { Code = "TEST_NATIVE_FAILURE", Stage = "Execute", Message = "Simulated command failure." } };
                if (command.Command == CadCommandNames.SavePart)
                {
                    var path = Path.GetFullPath(Path.Combine(_workspace, (string)command.Parameters["path"]));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, new byte[] { 1, 3, 3, 7 });
                    return CadCommandResult.Ok(new { path });
                }
                if (command.Command == CadCommandNames.GetBodyCount) return CadCommandResult.Ok(new { bodyCount = 1 });
                if (command.Command == CadCommandNames.GetBoundingBox) return CadCommandResult.Ok(new { sizeXmm = 40.0, sizeYmm = 20.0, sizeZmm = 5.0 });
                if (command.Command == CadCommandNames.GetRebuildErrors) return CadCommandResult.Ok(new { hasErrors = false });
                return CadCommandResult.Ok(new { completed = true });
            }
        }
    }
}
