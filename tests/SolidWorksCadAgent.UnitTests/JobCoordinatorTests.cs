using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;

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
    }
}
