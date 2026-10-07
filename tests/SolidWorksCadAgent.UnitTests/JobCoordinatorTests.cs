using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class JobCoordinatorTests
    {
        private sealed class CapturingPlanner : IImageCadPlanningProvider
        {
            public readonly System.Collections.Generic.List<CadPlanningRequest> Requests = new System.Collections.Generic.List<CadPlanningRequest>();
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token)
            {
                Requests.Add(request);
                return new DeterministicCadPlanningProvider().PlanAsync(request, token);
            }
        }

        [TestMethod]
        public async Task ImageStaysWithJobAcrossPlanRevision()
        {
            var planner = new CapturingPlanner();
            var coordinator = new JobCoordinator(_repository, planner, new SimulatedCadCommandExecutor(),
                new AgentSettings { WorkspaceRoot = Path.GetTempPath(), ExecutionMode = ExecutionMode.Simulation });
            var image = new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1, 2, 3 } };
            var initial = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None, image: image);
            Assert.AreEqual(JobState.AwaitingApproval, initial.Job.State);
            var revised = await coordinator.RequestChangesAsync(initial.Job.Id, initial.Revisions.Last().Id,
                "Make it 20 mm wider", CancellationToken.None);
            Assert.AreEqual(2, planner.Requests.Count);
            CollectionAssert.AreEqual(image.Bytes, planner.Requests[0].Image.Bytes);
            CollectionAssert.AreEqual(image.Bytes, planner.Requests[1].Image.Bytes);
            Assert.AreEqual(initial.Job.Id, revised.Job.Id);
        }

        [TestMethod]
        public async Task DeterministicPlannerRejectsImageInsteadOfPlanningFromTextOnly()
        {
            var coordinator = new JobCoordinator(_repository, new DeterministicCadPlanningProvider(),
                new SimulatedCadCommandExecutor(), new AgentSettings { WorkspaceRoot = Path.GetTempPath(), ExecutionMode = ExecutionMode.Simulation });
            Assert.IsFalse(coordinator.SupportsImageInputs);
            var error = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None,
                    image: new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1, 2, 3 } }));
            Assert.AreEqual("IMAGE_PLANNING_UNAVAILABLE", error.Code);
        }
        private string _databasePath;
        private string _workspace;
        private SqliteJobRepository _repository;

        [TestInitialize]
        public async Task Initialize()
        {
            _databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Coordinator-" + Guid.NewGuid().ToString("N") + ".db");
            _workspace = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Coordinator-" + Guid.NewGuid().ToString("N"));
            _repository = new SqliteJobRepository(_databasePath);
            await _repository.InitializeAsync();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _repository?.Dispose();
            TryDelete(_databasePath);
            TryDelete(_databasePath + "-wal");
            TryDelete(_databasePath + "-shm");
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        }

        [TestMethod]
        public async Task CreateAndPlanAsync_DefaultApprovalMode_PersistsPlanWithoutExecutingCad()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = CreateCoordinator(executor, false);

            var snapshot = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingApproval, snapshot.Job.State);
            Assert.IsTrue(snapshot.Job.PlanValidated);
            Assert.AreEqual(1, snapshot.Revisions.Count);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task SimulationSavePlan_RequiresClarificationBeforeApprovalOrExecution()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = await new DeterministicCadPlanningProvider().PlanAsync(
                new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None);
            plan.ProposedCommands.Add(new CadCommandEnvelope
            {
                Command = CadCommandNames.SavePart,
                Parameters = JObject.FromObject(new { path = "phone-tests/Plate.sldprt", allowOverwrite = false })
            });
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation, WorkspaceRoot = Path.GetTempPath() });

            var planned = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            Assert.IsFalse(planned.Job.PlanValidated);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "SavePart");
            Assert.AreEqual(0, executor.CallCount);
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(planned.Revisions.Single().PlanJson);
            Assert.AreEqual(plan.ProposedCommands.Count, persisted.ProposedCommands.Count);
            Assert.AreEqual(CadCommandNames.SavePart, persisted.ProposedCommands.Last().Command);
            Assert.AreEqual("phone-tests/Plate.sldprt", (string)persisted.ProposedCommands.Last().Parameters["path"]);
            Assert.IsFalse((bool)persisted.ProposedCommands.Last().Parameters["allowOverwrite"]);
        }

        [TestMethod]
        public async Task SimulationOpenPart_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(Command(CadCommandNames.OpenPart, new { path = "phone-tests/Existing.sldprt" }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation, WorkspaceRoot = Path.GetTempPath() });

            var planned = await coordinator.CreateAndPlanAsync("Open the existing part", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            Assert.IsFalse(planned.Job.PlanValidated);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "OpenPart");
            Assert.AreEqual(0, executor.CallCount);
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(planned.Revisions.Single().PlanJson);
            Assert.AreEqual(CadCommandNames.OpenPart, persisted.ProposedCommands.Single().Command);
            Assert.AreEqual("phone-tests/Existing.sldprt", (string)persisted.ProposedCommands.Single().Parameters["path"]);
        }

        [TestMethod]
        public async Task RealOpenPartPlan_RemainsAwaitingApprovalWithLegacyPath()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(Command(CadCommandNames.OpenPart, new { path = "phone-tests/Existing.sldprt" }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = false, ExecutionMode = ExecutionMode.Real, WorkspaceRoot = Path.GetTempPath() });

            var planned = await coordinator.CreateAndPlanAsync("Open the existing part", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
            Assert.IsTrue(planned.Job.PlanValidated);
            Assert.AreEqual(0, executor.CallCount);
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(planned.Revisions.Single().PlanJson);
            Assert.AreEqual(CadCommandNames.OpenPart, persisted.ProposedCommands.Single().Command);
            Assert.AreEqual("phone-tests/Existing.sldprt", (string)persisted.ProposedCommands.Single().Parameters["path"]);
            await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);
            Assert.IsTrue(executor.CallCount > 0, "A valid read-only OpenPart plan must remain executable after approval revalidation.");
        }

        [TestMethod]
        public async Task ApprovalRevalidatesPersistedOpenPartPlanBeforeAnyCadCommand()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(Command(CadCommandNames.OpenPart, new { path = "phone-tests/Existing.sldprt" }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = false, ExecutionMode = ExecutionMode.Real, WorkspaceRoot = _workspace });
            var planned = await coordinator.CreateAndPlanAsync("Open an existing part", CancellationToken.None);
            Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);

            // Represents an AwaitingApproval revision persisted before the read-only OpenPart rule existed.
            var historical = Plan(
                Command(CadCommandNames.OpenPart, new { path = "phone-tests/Existing.sldprt" }),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }));
            using (var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = _databasePath, Version = 3 }.ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE Revisions SET PlanJson = @plan WHERE Id = @id";
                    command.Parameters.AddWithValue("@plan", Newtonsoft.Json.JsonConvert.SerializeObject(historical));
                    command.Parameters.AddWithValue("@id", planned.Revisions.Single().Id.ToString("D"));
                    Assert.AreEqual(1, command.ExecuteNonQuery());
                }
            }

            var error = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None));
            Assert.AreEqual("INVALID_PLAN", error.Code);
            StringAssert.Contains(error.Message, "read-only");
            Assert.AreEqual(0, executor.CallCount);
            Assert.AreEqual(JobState.AwaitingApproval, (await _repository.GetAsync(planned.Job.Id)).State);
        }

        [TestMethod]
        public async Task SimulationCustomProfileBoss_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddSlot, new { centerXmm = 0.0, centerYmm = 0.0, lengthMm = 30.0, widthMm = 10.0, angleDegrees = 0.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 10.0 }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var planned = await coordinator.CreateAndPlanAsync("Extrude a slotted profile", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "Extrude");
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task SimulationCircleBoss_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddCircle, new { centerXmm = 0.0, centerYmm = 0.0, diameterMm = 12.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 10.0 }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var planned = await coordinator.CreateAndPlanAsync("Extrude a circular profile", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "rectangle profile");
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task SimulationCustomProfileCut_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 8.0 }),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddSlot, new { centerXmm = 0.0, centerYmm = 0.0, lengthMm = 12.0, widthMm = 4.0, angleDegrees = 0.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.CutExtrude, new { endCondition = "ThroughAll" }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var planned = await coordinator.CreateAndPlanAsync("Cut a slot through a plate", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "CutExtrude");
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task SimulationRectangleCut_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 8.0 }),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 10.0, heightMm = 4.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.CutExtrude, new { endCondition = "ThroughAll" }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var planned = await coordinator.CreateAndPlanAsync("Cut a rectangular opening", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "circular profile");
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task SimulationBlindCircleCut_RequiresClarificationBeforeExecutorCalls()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = Plan(
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude, new { depthMm = 8.0 }),
                Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                Command(CadCommandNames.AddCircle, new { centerXmm = 0.0, centerYmm = 0.0, diameterMm = 6.0 }),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.CutExtrude, new { endCondition = "Blind", depthMm = 2.0 }));
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var planned = await coordinator.CreateAndPlanAsync("Make a blind circular pocket", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, planned.Job.State);
            StringAssert.Contains(planned.Job.AmbiguityMessage, "Blind");
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task SimulationRectangleBossAndCircleThroughAllCut_StillExecute()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(_repository, new DeterministicCadPlanningProvider(), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var completed = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            Assert.AreEqual(JobState.ReadyForReview, completed.Job.State);
            Assert.IsTrue(executor.ExecutedCommands.Any(command => command.Command == CadCommandNames.Extrude));
            Assert.IsTrue(executor.ExecutedCommands.Any(command => command.Command == CadCommandNames.CutExtrude));
        }

        [TestMethod]
        public async Task SubmitAsync_ReturnsDurableIdentifierWithoutWaitingForPlanner()
        {
            var planner = new PendingPlanner();
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(_repository, planner, executor,
                new AgentSettings { ExecutionMode = ExecutionMode.Simulation });
            var submitted = await coordinator.SubmitAsync(AcceptancePrompt, CancellationToken.None);
            Assert.AreEqual(JobState.New, submitted.Job.State);
            Assert.IsNotNull(await _repository.GetAsync(submitted.Job.Id));
            try
            {
                Assert.AreEqual(planner.Started.Task, await Task.WhenAny(planner.Started.Task, Task.Delay(2000)));
                Assert.IsFalse(planner.Result.Task.IsCompleted);
                Assert.AreEqual(0, executor.ExecutedCommands.Count);
            }
            finally { planner.Result.TrySetResult(await new DeterministicCadPlanningProvider().PlanAsync(new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None)); }
            await coordinator.WaitForSubmittedJobsAsync();
            Assert.AreEqual(JobState.AwaitingApproval, (await _repository.GetAsync(submitted.Job.Id)).State);
        }

        [TestMethod]
        public async Task SubmitAsync_CancelDuringPlanningPreventsAutoExecution()
        {
            var planner = new PendingPlanner();
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = new JobCoordinator(_repository, planner, executor,
                new AgentSettings { ExecutionMode = ExecutionMode.Simulation, AutoMode = true });
            var submitted = await coordinator.SubmitAsync(AcceptancePrompt, CancellationToken.None);
            Assert.AreEqual(planner.Started.Task, await Task.WhenAny(planner.Started.Task, Task.Delay(2000)));
            var current = await _repository.GetAsync(submitted.Job.Id);
            var expected = current.State;
            new SolidWorksCadAgent.Core.Jobs.JobStateMachine().Transition(current, JobState.Cancelled);
            await _repository.TryUpdateFromStateAsync(current, expected, CancellationToken.None);
            planner.Result.TrySetResult(await new DeterministicCadPlanningProvider().PlanAsync(new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None));
            await coordinator.WaitForSubmittedJobsAsync();
            Assert.AreEqual(JobState.Cancelled, (await _repository.GetAsync(submitted.Job.Id)).State);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task SubmitAsync_PlannerFailurePersistsFailedState()
        {
            var planner = new PendingPlanner();
            var coordinator = new JobCoordinator(_repository, planner, new SimulatedCadCommandExecutor(), new AgentSettings());
            var submitted = await coordinator.SubmitAsync(AcceptancePrompt, CancellationToken.None);
            planner.Result.TrySetException(new InvalidOperationException("test planner failure"));
            await coordinator.WaitForSubmittedJobsAsync();
            Assert.AreEqual(JobState.Failed, (await _repository.GetAsync(submitted.Job.Id)).State);
        }

        [TestMethod]
        public async Task SubmitAsync_ExecutionFailurePersistsFailedState()
        {
            var coordinator = new JobCoordinator(_repository, new DeterministicCadPlanningProvider(),
                new ThrowingExecutor(), new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });
            var submitted = await coordinator.SubmitAsync(AcceptancePrompt, CancellationToken.None);
            await coordinator.WaitForSubmittedJobsAsync();
            Assert.AreEqual(JobState.Failed, (await _repository.GetAsync(submitted.Job.Id)).State);
        }

        [TestMethod]
        public async Task EnqueueApproval_PersistsApprovedBeforeBlockedExecutionAndDrainsFailure()
        {
            var executor = new PendingExecutor();
            var coordinator = new JobCoordinator(_repository, new DeterministicCadPlanningProvider(), executor,
                new AgentSettings { ExecutionMode = ExecutionMode.Simulation });
            var planned = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);
            var receipt = await coordinator.EnqueueApprovalAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);
            Assert.AreEqual(JobState.Approved, receipt.Job.State);
            Assert.AreEqual(executor.Started.Task, await Task.WhenAny(executor.Started.Task, Task.Delay(2000)));
            executor.Result.TrySetException(new InvalidOperationException("execution failure"));
            await coordinator.WaitForSubmittedJobsAsync();
            Assert.AreEqual(JobState.Failed, (await _repository.GetAsync(planned.Job.Id)).State);
        }

        [TestMethod]
        public async Task EnqueueChanges_PersistsInterpretingAndRejectsApprovalDuringReplan()
        {
            var planned = await CreateCoordinator(new SimulatedCadCommandExecutor(), false).CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);
            var planner = new PendingPlanner();
            var coordinator = new JobCoordinator(_repository, planner, new SimulatedCadCommandExecutor(), new AgentSettings());
            var receipt = await coordinator.EnqueueChangesAsync(planned.Job.Id, planned.Revisions.Single().Id, "Make it wider", CancellationToken.None);
            Assert.AreEqual(JobState.Interpreting, receipt.Job.State);
            try
            {
                await coordinator.EnqueueApprovalAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);
                Assert.Fail("Cannot approve while revision is planning");
            }
            catch (JobCoordinatorException ex) { Assert.AreEqual("INVALID_JOB_STATE", ex.Code); }
            planner.Result.TrySetResult(await new DeterministicCadPlanningProvider().PlanAsync(new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None));
            await coordinator.WaitForSubmittedJobsAsync();
            var revised = await _repository.GetSnapshotAsync(planned.Job.Id);
            Assert.AreEqual(2, revised.Revisions.Count);
            Assert.AreEqual(JobState.AwaitingApproval, revised.Job.State);
        }

        [TestMethod]
        public async Task EnqueueChanges_CancellationNeverRestoresApprovalOrAppendsRevision()
        {
            var planned = await CreateCoordinator(new SimulatedCadCommandExecutor(), false).CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);
            var planner = new PendingPlanner();
            var coordinator = new JobCoordinator(_repository, planner, new SimulatedCadCommandExecutor(), new AgentSettings());
            await coordinator.EnqueueChangesAsync(planned.Job.Id, planned.Revisions.Single().Id, "Wider", CancellationToken.None);
            await planner.Started.Task;
            var job = await _repository.GetAsync(planned.Job.Id);
            new SolidWorksCadAgent.Core.Jobs.JobStateMachine().Transition(job, JobState.Cancelled);
            Assert.IsTrue(await _repository.TryUpdateFromStateAsync(job, JobState.Interpreting));
            planner.Result.TrySetResult(await new DeterministicCadPlanningProvider().PlanAsync(new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None));
            await coordinator.WaitForSubmittedJobsAsync();
            var final = await _repository.GetSnapshotAsync(job.Id);
            Assert.AreEqual(JobState.Cancelled, final.Job.State);
            Assert.AreEqual(1, final.Revisions.Count);
        }

        private sealed class PendingExecutor : ICadCommandExecutor
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<CadCommandResult> Result = new TaskCompletionSource<CadCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            { Started.TrySetResult(true); return Result.Task; }
        }

        private sealed class ThrowingExecutor : ICadCommandExecutor
        {
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token) =>
                throw new InvalidOperationException("test execution failure");
        }

        private sealed class PendingPlanner : ICadPlanningProvider
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<CadPlanningResult> Result = new TaskCompletionSource<CadPlanningResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token)
            {
                Started.TrySetResult(true);
                return Result.Task;
            }
        }

        [TestMethod]
        public async Task ApproveAndExecuteAsync_CurrentRevision_BuildsVerifiesAndStopsReadyForReview()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = CreateCoordinator(executor, false);
            var planned = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            var completed = await coordinator.ApproveAndExecuteAsync(
                planned.Job.Id,
                planned.Revisions.Single().Id,
                CancellationToken.None);

            Assert.AreEqual(JobState.ReadyForReview, completed.Job.State);
            Assert.AreEqual(10, completed.Commands.Count(record => record.RevisionNumber == 1 && !record.CommandName.StartsWith("Get")));
            Assert.IsTrue(completed.Verifications.All(result => result.Passed));
            Assert.IsTrue(completed.Verifications.Count >= 2);
        }

        [TestMethod]
        public async Task ApproveAndExecuteAsync_StaleRevision_IsRejectedBeforeCadExecution()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = CreateCoordinator(executor, false);
            var planned = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            var error = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() =>
                coordinator.ApproveAndExecuteAsync(planned.Job.Id, Guid.NewGuid(), CancellationToken.None));

            Assert.AreEqual("STALE_PLAN", error.Code);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task CreateAndPlanAsync_AmbiguousPromptInAutoMode_StillRequiresClarification()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = CreateCoordinator(executor, true);

            var snapshot = await coordinator.CreateAndPlanAsync("Make a plate with an M8 hole", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.IsTrue(snapshot.Job.HasUnresolvedAmbiguity);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
        }

        [TestMethod]
        public async Task CreateAndPlanAsync_OverwriteRequestedByModel_CannotAutoExecute()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "Save over an existing part.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.SavePart,
                        Parameters = JObject.FromObject(new { path = "part.sldprt", allowOverwrite = true })
                    }
                }
            };
            var coordinator = new JobCoordinator(
                _repository,
                new FixedPlanningProvider(plan),
                executor,
                new AgentSettings { AutoMode = true });

            var snapshot = await coordinator.CreateAndPlanAsync("Replace the existing part", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingApproval, snapshot.Job.State);
            Assert.IsTrue(snapshot.Job.OverwriteRequested);
            Assert.IsFalse(snapshot.Job.OverwriteAuthorized);
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task CreateAndPlanAsync_InvalidCommandParameters_RequiresClarificationBeforeApproval()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "Rectangle with a misspelled private parameter.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.AddRectangle,
                        Parameters = JObject.FromObject(new { centreXmm = 0.0, centerYmm = 0.0, widthMm = 100.0, heightMm = 60.0 })
                    }
                }
            };
            var coordinator = new JobCoordinator(
                _repository,
                new FixedPlanningProvider(plan),
                executor,
                new AgentSettings { AutoMode = false });

            var snapshot = await coordinator.CreateAndPlanAsync("Create a rectangle", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.IsTrue(snapshot.Job.HasUnresolvedAmbiguity);
            Assert.AreEqual(0, executor.CallCount);
        }

        [TestMethod]
        public async Task TypedLegacySubset_InvalidCircleIsRejectedBeforeAnyExecutorCall()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "Invalid circle diameter.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.AddCircle, Parameters = JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, diameterMm = 0.0 }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var snapshot = await coordinator.CreateAndPlanAsync("Create a part with a circle", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(snapshot.Job.AmbiguityMessage, "diameterMm");
        }

        [TestMethod]
        public async Task TypedLegacySubsetExecutesWithSamePersistedAndDispatchedParameters()
        {
            var executor = new CapturingSuccessfulExecutor(_workspace);
            var plan = new CadPlanningResult
            {
                Summary = "A typed-subset smoke plan.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddCircle, Parameters = JObject.FromObject(new { centerXmm = 2.5, centerYmm = -4.0, diameterMm = 8.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddLine, Parameters = JObject.FromObject(new { startXmm = -12.5, startYmm = 3.0, endXmm = 4.25, endYmm = 9.5 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.ExitSketch, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.Extrude, Parameters = JObject.FromObject(new { depthMm = 10.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.SavePart, Parameters = JObject.FromObject(new { path = "typed-operation.sldprt" }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = false, ExecutionMode = ExecutionMode.Real, WorkspaceRoot = _workspace });

            var planned = await coordinator.CreateAndPlanAsync("Create a circular boss", CancellationToken.None);
            Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
            Assert.IsFalse(planned.Job.IsSimulated);
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(planned.Revisions.Single().PlanJson);
            Assert.AreEqual(7, persisted.ProposedCommands.Count);
            Assert.IsNull(JObject.Parse(planned.Revisions.Single().PlanJson)["ProposedCommands"][0]["operationVersion"]);

            var completed = await coordinator.ApproveAndExecuteAsync(planned.Job.Id, planned.Revisions.Single().Id, CancellationToken.None);
            Assert.AreEqual(JobState.ReadyForReview, completed.Job.State);
            Assert.AreEqual(CadCommandNames.NewPart, executor.Received[0].Command);
            Assert.AreEqual(CadCommandNames.CreateSketch, executor.Received[1].Command);
            Assert.AreEqual("Top Plane", (string)executor.Received[1].Parameters["plane"]);
            Assert.AreEqual(CadCommandNames.AddCircle, executor.Received[2].Command);
            Assert.AreEqual(plan.ProposedCommands[2].Parameters.ToString(), executor.Received[2].Parameters.ToString());
            Assert.AreEqual(CadCommandNames.AddLine, executor.Received[3].Command);
            Assert.AreEqual(plan.ProposedCommands[3].Parameters.ToString(), executor.Received[3].Parameters.ToString());
            Assert.AreEqual(planned.Job.Id, executor.Received[0].ExecutionId);
        }

        [TestMethod]
        public async Task TypedLegacySubset_DegenerateLineIsRejectedBeforeAnyExecutorCall()
        {
            var executor = new RecordingSuccessfulExecutor(_workspace);
            var plan = new CadPlanningResult
            {
                Summary = "A zero-length line is invalid.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddLine, Parameters = JObject.FromObject(new { startXmm = 1.0, startYmm = 2.0, endXmm = 1.0, endYmm = 2.0 }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var snapshot = await coordinator.CreateAndPlanAsync("Draw a line with identical endpoints", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(snapshot.Job.AmbiguityMessage, "Start and end");
        }

        [TestMethod]
        public async Task InvalidLifecyclePlanIsRejectedBeforeAnyExecutorCall()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "The sketch must close before the part is saved.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddRectangle, Parameters = JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.SavePart, Parameters = JObject.FromObject(new { path = "open-sketch.sldprt" }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var snapshot = await coordinator.CreateAndPlanAsync("Create and save a rectangle", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(snapshot.Job.AmbiguityMessage, "ExitSketch");
        }

        [TestMethod]
        public async Task FeatureWithoutSupportedClosedProfileIsRejectedBeforeAnyExecutorCall()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "A line-only sketch has no profile with proven closure.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddLine, Parameters = JObject.FromObject(new { startXmm = 0.0, startYmm = 0.0, endXmm = 20.0, endYmm = 0.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.ExitSketch, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.Extrude, Parameters = JObject.FromObject(new { depthMm = 10.0 }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var snapshot = await coordinator.CreateAndPlanAsync("Extrude an open line", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(snapshot.Job.AmbiguityMessage, "supported closed profile primitive");
        }

        [TestMethod]
        public async Task ModelChangeAfterFinalSaveIsRejectedBeforeAnyExecutorCall()
        {
            var executor = new RecordingSuccessfulExecutor();
            var plan = new CadPlanningResult
            {
                Summary = "A model change cannot follow the final saved artifact.",
                ProposedCommands = new System.Collections.Generic.List<CadCommandEnvelope>
                {
                    new CadCommandEnvelope { Command = CadCommandNames.NewPart, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.AddRectangle, Parameters = JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 40.0, heightMm = 20.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.ExitSketch, Parameters = new JObject() },
                    new CadCommandEnvelope { Command = CadCommandNames.Extrude, Parameters = JObject.FromObject(new { depthMm = 10.0 }) },
                    new CadCommandEnvelope { Command = CadCommandNames.SavePart, Parameters = JObject.FromObject(new { path = "finalized-part.sldprt" }) },
                    new CadCommandEnvelope { Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }) }
                }
            };
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(plan), executor,
                new AgentSettings { AutoMode = true, ExecutionMode = ExecutionMode.Simulation });

            var snapshot = await coordinator.CreateAndPlanAsync("Create, save, then modify a part", CancellationToken.None);

            Assert.AreEqual(JobState.AwaitingClarification, snapshot.Job.State);
            Assert.AreEqual(0, executor.CallCount);
            StringAssert.Contains(snapshot.Job.AmbiguityMessage, "final model-changing operation");
        }

        [TestMethod]
        public async Task ApproveAndExecuteAsync_TwoJobs_NeverExecuteCadCommandsConcurrently()
        {
            var executor = new BlockingSuccessfulExecutor();
            var coordinator = new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                executor,
                new AgentSettings { AutoMode = false });
            var first = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);
            var second = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);

            var firstRun = coordinator.ApproveAndExecuteAsync(first.Job.Id, first.Revisions.Single().Id, CancellationToken.None);
            await executor.FirstCommandStarted;
            var secondRun = coordinator.ApproveAndExecuteAsync(second.Job.Id, second.Revisions.Single().Id, CancellationToken.None);
            await Task.Delay(150);
            executor.Release();
            await Task.WhenAll(firstRun, secondRun);

            Assert.AreEqual(1, executor.MaximumConcurrentCalls);
        }

        private JobCoordinator CreateCoordinator(SimulatedCadCommandExecutor executor, bool autoMode)
        {
            return new JobCoordinator(
                _repository,
                new DeterministicCadPlanningProvider(),
                executor,
                new AgentSettings { AutoMode = autoMode });
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

        [TestMethod]
        public async Task RequestChanges_AfterReviewRebuildsNewPartWithSeparateSavePathAndApproval()
        {
            var executor = new RecordingSuccessfulExecutor();
            var savedPlan = await new DeterministicCadPlanningProvider().PlanAsync(new CadPlanningRequest { Prompt = AcceptancePrompt }, CancellationToken.None);
            savedPlan.ProposedCommands.Add(new CadCommandEnvelope { Command = CadCommandNames.SavePart,
                Parameters = JObject.FromObject(new { path = "phone-tests/Plate.sldprt", allowOverwrite = false }) });
            var coordinator = new JobCoordinator(_repository, new FixedPlanningProvider(savedPlan), executor,
                new AgentSettings { ExecutionMode = ExecutionMode.Real, WorkspaceRoot = _workspace });
            var first = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None);
            var built = await coordinator.ApproveAndExecuteAsync(first.Job.Id, first.Revisions.Single().Id, CancellationToken.None);
            var count = executor.CallCount;
            var revised = await coordinator.RequestChangesAsync(first.Job.Id, built.Revisions.Last().Id, "Make a revised version", CancellationToken.None);
            Assert.AreEqual(JobState.AwaitingApproval, revised.Job.State);
            Assert.AreEqual(count, executor.CallCount);
            var plan = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(revised.Revisions.Last().PlanJson);
            Assert.AreEqual(CadCommandNames.NewPart, plan.ProposedCommands.First().Command);
            var oldPlan = Newtonsoft.Json.JsonConvert.DeserializeObject<CadPlanningResult>(first.Revisions.Single().PlanJson);
            Assert.AreNotEqual((string)oldPlan.ProposedCommands.Single(c => c.Command == CadCommandNames.SavePart).Parameters["path"],
                (string)plan.ProposedCommands.Single(c => c.Command == CadCommandNames.SavePart).Parameters["path"]);
            Assert.IsFalse((bool)plan.ProposedCommands.Single(c => c.Command == CadCommandNames.SavePart).Parameters["allowOverwrite"]);
            savedPlan.ProposedCommands.Insert(1, new CadCommandEnvelope { Command = CadCommandNames.OpenPart,
                Parameters = JObject.FromObject(new { path = "phone-tests/Plate.sldprt" }) });
            var unsafeRevision = await coordinator.RequestChangesAsync(first.Job.Id, revised.Revisions.Last().Id, "Keep changing this revision", CancellationToken.None);
            Assert.AreEqual(JobState.AwaitingClarification, unsafeRevision.Job.State);
            StringAssert.Contains(unsafeRevision.Job.AmbiguityMessage, "cannot open an existing part");
            Assert.AreEqual(count, executor.CallCount);
        }

        [TestMethod]
        public async Task ImageDesignPlan_PersistsManualApprovalRequirement_DespiteAutoMode()
        {
            var executor = new SimulatedCadCommandExecutor();
            var coordinator = CreateCoordinator(executor, true);
            var snapshot = await coordinator.CreateAndPlanAsync(AcceptancePrompt, CancellationToken.None, true);
            Assert.AreEqual(JobState.AwaitingApproval, snapshot.Job.State);
            Assert.AreEqual(0, executor.ExecutedCommands.Count);
            Assert.IsTrue((await _repository.GetAsync(snapshot.Job.Id)).RequiresExplicitApproval);
            Assert.IsFalse(SolidWorksCadAgent.Core.Jobs.ApprovalPolicy.CanExecute(
                (await _repository.GetAsync(snapshot.Job.Id)), new AgentSettings
                {
                    AutoMode = true
                }
));
            var executed = await coordinator.ApproveAndExecuteAsync(snapshot.Job.Id, snapshot.Revisions.Last().Id, CancellationToken.None);
            Assert.AreEqual(JobState.ReadyForReview, executed.Job.State);
            Assert.IsTrue(executor.ExecutedCommands.Count > 0);
        }

        private const string AcceptancePrompt =
            "Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.";

        private static CadPlanningResult Plan(params CadCommandEnvelope[] commands) => new CadPlanningResult
        {
            Summary = "A simulation capability preflight test plan.",
            ProposedCommands = commands.ToList()
        };

        private static CadCommandEnvelope Command(string name, object parameters = null) => new CadCommandEnvelope
        {
            Command = name,
            Parameters = parameters == null ? new JObject() : JObject.FromObject(parameters)
        };

        private sealed class FixedPlanningProvider : ICadPlanningProvider
        {
            private readonly CadPlanningResult _plan;
            public FixedPlanningProvider(CadPlanningResult plan) { _plan = plan; }
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(_plan);
        }

        private class RecordingSuccessfulExecutor : ICadCommandExecutor
        {
            private readonly string _workspace;
            public RecordingSuccessfulExecutor(string workspace = null)
            {
                _workspace = workspace ?? Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-FakeSave-" + Guid.NewGuid().ToString("N"));
            }
            public int CallCount { get; protected set; }
            public virtual Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                CallCount = _callCount;
                if (command.Command == CadCommandNames.SavePart)
                {
                    var requestedPath = command.Parameters?["path"]?.Value<string>() ?? "fake-save.sldprt";
                    if (Path.IsPathRooted(requestedPath)) requestedPath = Path.GetFileName(requestedPath);
                    var path = Path.GetFullPath(Path.Combine(_workspace, requestedPath));
                    var workspacePrefix = Path.GetFullPath(_workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
                        path = Path.Combine(_workspace, Path.GetFileName(requestedPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, new byte[] { 83, 87, 65, 71 });
                    return Task.FromResult(CadCommandResult.Ok(new { path }));
                }
                return Task.FromResult(ResultFor(command.Command));
            }

            private int _callCount;

            protected static CadCommandResult ResultFor(string command)
            {
                if (command == CadCommandNames.GetBodyCount) return CadCommandResult.Ok(new { BodyCount = 1 });
                if (command == CadCommandNames.GetBoundingBox) return CadCommandResult.Ok(new { SizeXmm = 100.0, SizeYmm = 60.0, SizeZmm = 10.0 });
                if (command == CadCommandNames.GetRebuildErrors) return CadCommandResult.Ok(new { HasErrors = false });
                return CadCommandResult.Ok(new { Completed = true });
            }
        }

        private sealed class CapturingSuccessfulExecutor : RecordingSuccessfulExecutor
        {
            public CapturingSuccessfulExecutor(string workspace = null) : base(workspace) { }
            public System.Collections.Generic.List<CadCommandEnvelope> Received { get; } = new System.Collections.Generic.List<CadCommandEnvelope>();

            public override Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
            {
                Received.Add(new CadCommandEnvelope
                {
                    Command = command.Command,
                    Parameters = command.Parameters == null ? new JObject() : (JObject)command.Parameters.DeepClone(),
                    ExecutionId = command.ExecutionId
                });
                return base.ExecuteAsync(command, cancellationToken);
            }
        }

        private sealed class BlockingSuccessfulExecutor : RecordingSuccessfulExecutor
        {
            private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
            private int _current;
            private int _maximum;

            public Task FirstCommandStarted => _started.Task;
            public int MaximumConcurrentCalls => _maximum;
            public void Release() => _release.TrySetResult(true);

            public override async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
            {
                var current = Interlocked.Increment(ref _current);
                var observed = _maximum;
                while (current > observed)
                {
                    var original = Interlocked.CompareExchange(ref _maximum, current, observed);
                    if (original == observed) break;
                    observed = original;
                }
                _started.TrySetResult(true);
                try
                {
                    await _release.Task;
                    return await base.ExecuteAsync(command, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _current);
                }
            }
        }
    }
}
