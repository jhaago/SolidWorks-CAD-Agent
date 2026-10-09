using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadV2SixStepRunnerTests
    {
        private const string Candidate = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"rectangle\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}," +
            "{\"stepKey\":\"save\",\"command\":\"SavePart\",\"operationVersion\":1,\"parameters\":{\"path\":\"v2-owned.sldprt\",\"allowOverwrite\":false}}]}";

        [TestMethod]
        public async Task RunnerCommitsSixOrderedStepsAndSavedVerification()
        {
            await WithJobAsync(async (root, repository, jobId, revisionId) =>
            {
                var executor = new RecordingExecutor(repository, root);
                var passed = await new CadV2SixStepRunner(repository, executor, new WorkspacePolicy(root))
                    .RunAsync(jobId, revisionId, CancellationToken.None);
                Assert.IsTrue(passed);
                var snapshot = await repository.GetSnapshotAsync(jobId, CancellationToken.None);
                CollectionAssert.AreEqual(new[] { "NewPart", "CreateSketch", "AddRectangle", "ExitSketch", "Extrude", "SavePart" },
                    snapshot.Commands.Select(command => command.CommandName).ToArray());
                Assert.AreEqual(6, executor.Mutations);
                Assert.AreEqual(CadV2SavedVerificationRecorder.CheckName, snapshot.Verifications.Last().CheckName);
                Assert.IsTrue(snapshot.Verifications.Last().Passed);
            });
        }

        [TestMethod]
        public async Task CoordinatorRequiresApprovalThenExecutesOnlyCurrentRealSixStepPlan()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var executor = new RecordingExecutor(repository, root);
                var coordinator = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real, AutoMode = true },
                    () => CadNativeControlLease.TryAcquire(Path.Combine(root, "native.lock")));
                var planned = await coordinator.CreateAndPlanAsync("Create 20 by 10 by 5 mm part", CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
                Assert.IsTrue(planned.Job.PlanValidated);
                Assert.IsTrue(planned.Job.RequiresExplicitApproval);
                Assert.AreEqual(0, executor.Mutations);
                var wrong = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                    coordinator.ApproveAndExecuteAsync(planned.Job.Id, Guid.NewGuid(), CancellationToken.None));
                Assert.AreEqual("STALE_PLAN", wrong.Code);
                Assert.AreEqual(0, executor.Mutations);
                var completed = await coordinator.ApproveAndExecuteAsync(planned.Job.Id,
                    planned.Revisions.Single().Id, CancellationToken.None);
                Assert.AreEqual(JobState.ReadyForReview, completed.Job.State);
                Assert.AreEqual(6, completed.Commands.Count);
                Assert.IsTrue(completed.Verifications.Last().Passed);
                Assert.AreEqual(Path.Combine(root, "v2-owned.sldprt"), completed.Job.OutputPath);
            });
        }

        [TestMethod]
        public async Task CancellationAfterApprovalWhileWaitingForExecutionGateFailsJobAndReleasesLease()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var leasePath = Path.Combine(root, "native.lock");
                var executor = new RecordingExecutor(repository, root);
                var coordinator = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real },
                    () => CadNativeControlLease.TryAcquire(leasePath));
                var planned = await coordinator.CreateAndPlanAsync("Create part", CancellationToken.None);
                var gate = (SemaphoreSlim)typeof(JobCoordinator)
                    .GetField("_executionGate", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).GetValue(coordinator);
                await gate.WaitAsync();
                try
                {
                    using (var cancel = new CancellationTokenSource())
                    {
                        var approval = coordinator.ApproveAndExecuteAsync(planned.Job.Id,
                            planned.Revisions.Single().Id, cancel.Token);
                        for (var attempt = 0; attempt < 100 &&
                            (await repository.GetAsync(planned.Job.Id)).State != JobState.Approved; attempt++)
                            await Task.Delay(10);
                        Assert.AreEqual(JobState.Approved, (await repository.GetAsync(planned.Job.Id)).State);
                        cancel.Cancel();
                        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => approval);
                    }
                }
                finally { gate.Release(); }

                Assert.AreEqual(JobState.Failed, (await repository.GetAsync(planned.Job.Id)).State);
                Assert.AreEqual(0, executor.Mutations);
                using (var lease = CadNativeControlLease.TryAcquire(leasePath))
                    Assert.IsNotNull(lease);
            });
        }

        [TestMethod]
        public async Task QueuedApprovalTransfersLeaseToWorkerAndReturnsApprovedReceipt()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var leasePath = Path.Combine(root, "native.lock");
                var executor = new RecordingExecutor(repository, root);
                var coordinator = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real },
                    () => CadNativeControlLease.TryAcquire(leasePath));
                var planned = await coordinator.CreateAndPlanAsync("Create part", CancellationToken.None);
                var gate = (SemaphoreSlim)typeof(JobCoordinator)
                    .GetField("_executionGate", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).GetValue(coordinator);
                await gate.WaitAsync();
                try
                {
                    var receipt = await coordinator.EnqueueApprovalAsync(planned.Job.Id,
                        planned.Revisions.Single().Id, CancellationToken.None);
                    Assert.AreEqual(JobState.Approved, receipt.Job.State);
                    using (var manual = CadNativeControlLease.TryAcquire(leasePath))
                        Assert.IsNull(manual);
                    Assert.AreEqual(0, executor.Mutations);
                }
                finally { gate.Release(); }

                await coordinator.WaitForSubmittedJobsAsync();
                Assert.AreEqual(JobState.ReadyForReview, (await repository.GetAsync(planned.Job.Id)).State);
                using (var manual = CadNativeControlLease.TryAcquire(leasePath))
                    Assert.IsNotNull(manual);
            });
        }

        [TestMethod]
        public async Task CoordinatorKeepsSimulationAndExistingSaveTargetsClosed()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var executor = new RecordingExecutor(repository, root);
                var simulation = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Simulation });
                var simulated = await simulation.CreateAndPlanAsync("Create part", CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingClarification, simulated.Job.State);
                File.WriteAllText(Path.Combine(root, "v2-owned.sldprt"), "owned pre-existing file");
                var real = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real });
                var blocked = await real.CreateAndPlanAsync("Create another part", CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingClarification, blocked.Job.State);
                Assert.AreEqual(0, executor.Mutations);
                Assert.AreEqual("owned pre-existing file", File.ReadAllText(Path.Combine(root, "v2-owned.sldprt")));
            });
        }

        [TestMethod]
        public async Task ManualLeaseBlocksApprovalWithoutStrandingOrMutatingJob()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var path = Path.Combine(root, "native.lock");
                var executor = new RecordingExecutor(repository, root);
                var coordinator = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real },
                    () => CadNativeControlLease.TryAcquire(path));
                var planned = await coordinator.CreateAndPlanAsync("Create part", CancellationToken.None);
                using (var manual = CadNativeControlLease.TryAcquire(path))
                {
                    Assert.IsNotNull(manual);
                    var blocked = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                        coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None));
                    Assert.AreEqual("MANUAL_CONTROL_ACTIVE", blocked.Code);
                    Assert.AreEqual(JobState.AwaitingApproval, (await repository.GetAsync(planned.Job.Id)).State);
                    Assert.AreEqual(0, executor.Mutations);
                }
                var done = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id,
                    CancellationToken.None);
                Assert.AreEqual(JobState.ReadyForReview, done.Job.State);
            });
        }

        [TestMethod]
        public async Task FailedNativeStepRecordsFailureAndNeverDispatchesSave()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var executor = new RecordingExecutor(repository, root) { FailCommand = CadCommandNames.Extrude };
                var coordinator = new JobCoordinator(repository, new FixedVersionedPlanner(Candidate), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real },
                    () => CadNativeControlLease.TryAcquire(Path.Combine(root, "native.lock")));
                var planned = await coordinator.CreateAndPlanAsync("Create part", CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None));
                var failed = await repository.GetSnapshotAsync(planned.Job.Id, CancellationToken.None);
                Assert.AreEqual(JobState.Failed, failed.Job.State);
                Assert.AreEqual(5, failed.Commands.Count);
                Assert.IsFalse(failed.Commands.Last().Success);
                Assert.AreEqual("V2_STEP_FAILED", failed.Commands.Last().ErrorCode);
                Assert.AreEqual(CadCommandNames.Extrude, failed.Commands.Last().CommandName);
                Assert.IsFalse(File.Exists(Path.Combine(root, "v2-owned.sldprt")));
                Assert.AreEqual(5, executor.Mutations);
            });
        }

        [TestMethod]
        public async Task ExplicitV2PromptUsesExistingJobInputWithoutCallingLegacyPlanner()
        {
            await WithJobAsync(async (root, repository, _, __) =>
            {
                var executor = new RecordingExecutor(repository, root);
                var coordinator = new JobCoordinator(repository, new FailingLegacyPlanner(), executor,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real },
                    () => CadNativeControlLease.TryAcquire(Path.Combine(root, "native.lock")));
                var invalid = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                    coordinator.CreateAndPlanAsync("V2 rectangle boss: 20 x 10 mm; save as part.sldprt", CancellationToken.None));
                Assert.AreEqual("V2_REQUEST_INVALID", invalid.Code);
                var planned = await coordinator.CreateAndPlanAsync(
                    "V2 rectangle boss: 20 x 10 x 5 mm; save as v2-owned.sldprt", CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
                Assert.AreEqual(2, JObject.Parse(planned.Revisions.Last().PlanJson).Value<int>("planVersion"));
                Assert.AreEqual(0, executor.Mutations);
                var revisionError = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                    coordinator.RequestChangesAsync(planned.Job.Id, planned.Revisions.Last().Id,
                        "Make it wider", CancellationToken.None));
                Assert.AreEqual("V2_CHANGE_UNSUPPORTED", revisionError.Code);
                Assert.AreEqual(JobState.AwaitingApproval, (await repository.GetAsync(planned.Job.Id)).State);
                var done = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Last().Id,
                    CancellationToken.None);
                Assert.AreEqual(JobState.ReadyForReview, done.Job.State);
            });
        }

        private sealed class FailingLegacyPlanner : ICadPlanningProvider
        {
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token) =>
                throw new AssertFailedException("The explicit v2 request must not use the legacy planner.");
        }

        private sealed class FixedVersionedPlanner : IVersionedCadPlanningProvider
        {
            private readonly string _candidate;
            public FixedVersionedPlanner(string candidate) { _candidate = candidate; }
            public Task<string> PlanDocumentAsync(CadPlanningRequest request, CancellationToken token) => Task.FromResult(_candidate);
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token) =>
                throw new AssertFailedException("Versioned planner must be used.");
        }

        [TestMethod]
        public async Task RunnerRejectsExistingSaveBeforeCreatingPart()
        {
            await WithJobAsync(async (root, repository, jobId, revisionId) =>
            {
                File.WriteAllText(Path.Combine(root, "v2-owned.sldprt"), "do not overwrite");
                var executor = new RecordingExecutor(repository, root);
                await Assert.ThrowsExceptionAsync<WorkspacePolicyException>(() =>
                    new CadV2SixStepRunner(repository, executor, new WorkspacePolicy(root))
                        .RunAsync(jobId, revisionId, CancellationToken.None));
                Assert.AreEqual(0, executor.Mutations);
                Assert.AreEqual("do not overwrite", File.ReadAllText(Path.Combine(root, "v2-owned.sldprt")));
            });
        }

        [TestMethod]
        public async Task RunnerRejectsUnknownRevisionBeforeAnyMutation()
        {
            var root = Path.Combine(Path.GetTempPath(), "cad-v2-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using (var repository = new SqliteJobRepository(Path.Combine(root, "jobs.sqlite")))
                {
                    await repository.InitializeAsync();
                    var runner = new CadV2SixStepRunner(repository, null, new WorkspacePolicy(root));
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        runner.RunAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task WithJobAsync(Func<string, SqliteJobRepository, Guid, Guid, Task> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "cad-v2-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using (var repository = new SqliteJobRepository(Path.Combine(root, "jobs.sqlite")))
                {
                    await repository.InitializeAsync();
                    var jobId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var now = DateTime.UtcNow;
                    await repository.CreateAsync(new CadJob { Id = jobId, Prompt = "owned rectangle",
                        State = JobState.Executing, PlanValidated = true, IsSimulated = false,
                        CreatedUtc = now, UpdatedUtc = now });
                    var normalized = CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(Candidate).CandidateV2);
                    await repository.AppendRevisionAsync(new JobRevision { Id = revisionId, JobId = jobId,
                        RevisionNumber = 1, Prompt = "owned rectangle", PlanJson = JsonConvert.SerializeObject(normalized),
                        CreatedUtc = now });
                    await action(root, repository, jobId, revisionId);
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class RecordingExecutor : ICadCommandExecutor, IVersionedCadCommandExecutor, ICadOperationAvailability
        {
            private readonly SqliteJobRepository _repository;
            private readonly string _root;
            public int Mutations { get; private set; }
            public string FailCommand { get; set; }
            public bool SupportsManagedReferences => true;
            public bool IsOperationRegistered(string name, int version) => true;
            public RecordingExecutor(SqliteJobRepository repository, string root) { _repository = repository; _root = root; }
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            {
                if (command.Command == CadCommandNames.GetRebuildErrors) return Task.FromResult(CadCommandResult.Ok(new { hasErrors = false }));
                if (command.Command == CadCommandNames.GetBodyCount) return Task.FromResult(CadCommandResult.Ok(new { bodyCount = 1, volumeMm3 = 1000.0 }));
                if (command.Command == CadCommandNames.GetBoundingBox) return Task.FromResult(CadCommandResult.Ok(new
                {
                    minXmm = -10.0, maxXmm = 10.0, minYmm = 0.0, maxYmm = 5.0,
                    minZmm = -5.0, maxZmm = 5.0, sizeXmm = 20.0, sizeYmm = 5.0, sizeZmm = 10.0
                }));
                if (command.Command == CadCommandNames.CloseDocument) return Task.FromResult(CadCommandResult.Ok(new { closed = true }));
                if (command.Command == CadCommandNames.OpenPart) return Task.FromResult(CadCommandResult.Ok(new
                {
                    path = new WorkspacePolicy(_root).ResolveForRead(command.Parameters.Value<string>("path")), errors = 0, warnings = 0
                }));
                throw new InvalidOperationException("Unexpected observation: " + command.Command);
            }
            public async Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken token)
            {
                Mutations++;
                var command = request.Command;
                if (command.Command == FailCommand) throw new InvalidOperationException("Fixture native failure.");
                if (command.Command == CadCommandNames.NewPart) return CadCommandResult.Ok(new
                {
                    modelId = command.ManagedModelId.Value.ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                });
                if (command.Command == CadCommandNames.CreateSketch)
                {
                    var binding = await _repository.GetEntityBindingAsync(command.ManagedModelId.Value,
                        command.OutputEntityId.Value, "Default", token);
                    binding.NativeReferenceBytes = new byte[] { 4, 5, 6 };
                    binding.Status = CadEntityReferenceStatus.Active;
                    binding.UpdatedUtc = DateTime.UtcNow;
                    await _repository.UpdateEntityBindingAsync(binding, token);
                    return CadCommandResult.Ok(new { entityId = command.OutputEntityId.Value.ToString("D") });
                }
                if (command.Command == CadCommandNames.AddRectangle)
                    return CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 });
                if (command.Command == CadCommandNames.ExitSketch) return CadCommandResult.Ok(new { exited = true });
                if (command.Command == CadCommandNames.Extrude) return CadCommandResult.Ok(new { depthMm = 5.0, featureName = "Boss-Extrude1" });
                if (command.Command == CadCommandNames.SavePart)
                {
                    var path = new WorkspacePolicy(_root).ResolveForWrite(command.Parameters.Value<string>("path"), false);
                    var stage = CadV2SavePath.StageFor(path, command.ManagedModelId.Value);
                    File.WriteAllText(stage, "owned fixture artifact");
                    File.Copy(stage, path, false);
                    return CadCommandResult.Ok(new { path, stagingPath = stage, errors = 0, warnings = 0 });
                }
                throw new InvalidOperationException("Unexpected mutation: " + command.Command);
            }
        }
    }
}
