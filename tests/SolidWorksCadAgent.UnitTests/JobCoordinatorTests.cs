using System;
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
        private string _databasePath;
        private SqliteJobRepository _repository;

        [TestInitialize]
        public async Task Initialize()
        {
            _databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Coordinator-" + Guid.NewGuid().ToString("N") + ".db");
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

        private const string AcceptancePrompt =
            "Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.";

        private sealed class FixedPlanningProvider : ICadPlanningProvider
        {
            private readonly CadPlanningResult _plan;
            public FixedPlanningProvider(CadPlanningResult plan) { _plan = plan; }
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(_plan);
        }

        private class RecordingSuccessfulExecutor : ICadCommandExecutor
        {
            public int CallCount { get; protected set; }
            public virtual Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                CallCount = _callCount;
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
