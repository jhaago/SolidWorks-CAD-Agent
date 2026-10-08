using System;
using System.IO;
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
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadV2MutationAttemptStoreTests
    {
        private const string Candidate = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"rectangle\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}]}";

        [TestMethod]
        public async Task PreparedAttemptUsesStoredCurrentRevisionAndPersistsAcrossRestart()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var first = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, first.Status);
                Assert.AreEqual(model.ModelId, first.ModelId);
                Assert.AreEqual(job.Item2, first.RevisionId);
                Assert.AreEqual(model.CurrentModelRevisionId, first.ProspectiveModelRevisionId);
                Assert.AreEqual(64, first.PlanSha256.Length);

                Assert.AreEqual(job.Item1,
                    await repository.GetV2ModelOwnerAsync(model.ModelId, CancellationToken.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None));

                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    var stored = await reopened.FindV2MutationAttemptAsync(
                        job.Item1, job.Item2, "new", CancellationToken.None);
                    Assert.AreEqual(first.Id, stored.Id);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, stored.Status);
                    Assert.AreEqual(first.PlanSha256, stored.PlanSha256);
                    Assert.IsNull(stored.OutputEntityId);
                    await reopened.MarkV2MutationAttemptUncertainAsync(first.Id, CancellationToken.None);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(first.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await reopened.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                }
            });
        }

        [TestMethod]
        public async Task AttemptRejectsStaleRevisionWrongStepAndCrossJobModel()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "sketch", model, CancellationToken.None));
                Assert.IsNull(await repository.GetV2ModelOwnerAsync(model.ModelId, CancellationToken.None));
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);

                var other = await AddJobAsync(repository, NormalizedPlan());
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2NewPartAttemptAsync(other.Item1, other.Item2, "new", model, CancellationToken.None));

                await repository.AppendRevisionAsync(new JobRevision
                {
                    Id = Guid.NewGuid(), JobId = job.Item1, RevisionNumber = 2, Prompt = "revised",
                    PlanJson = NormalizedPlan(), CreatedUtc = DateTime.UtcNow
                });
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", NewModel(), CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task VerifiedNewPartOutcomeAtomicallyAppliesAttemptAndActivatesOwnedModel()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var result = CadCommandResult.Ok(new
                {
                    modelId = model.ModelId.ToString("D"),
                    documentTitle = "Part1",
                    configurationKey = "Default"
                });

                await repository.CommitV2NewPartOutcomeAsync(
                    job.Item1, job.Item2, "new", model.ModelId, result, CancellationToken.None);

                var applied = await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied, applied.Status);
                var active = await repository.GetModelAsync(model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved, active.Status);
                Assert.AreEqual("Default", active.ConfigurationKey);
                Assert.AreEqual(model.CurrentModelRevisionId, active.CurrentModelRevisionId);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.CommitV2NewPartOutcomeAsync(
                        job.Item1, job.Item2, "new", model.ModelId, result, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task FailedOrMismatchedOutcomeCannotApplyAndDatabaseFailureRollsBackModelUpdate()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var failedResult = new CadCommandResult { Success = false, Error = new CadError { Code = "NEW_PART_FAILED" } };
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                        failedResult, CancellationToken.None));
                var result = CadCommandResult.Ok(new
                {
                    modelId = Guid.NewGuid().ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                });
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId, result, CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Pending,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);

                using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
                using (var command = connection.CreateCommand())
                {
                    connection.Open();
                    command.CommandText = "CREATE TRIGGER FailAppliedAttempt BEFORE UPDATE ON V2MutationAttempts WHEN NEW.Status = 'Applied' BEGIN SELECT RAISE(ABORT, 'forced commit failure'); END;";
                    command.ExecuteNonQuery();
                }
                var matching = CadCommandResult.Ok(new
                {
                    modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                });
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() =>
                    repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId, matching, CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Pending,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                await repository.MarkV2MutationAttemptUncertainAsync(attempt.Id, CancellationToken.None);
            });
        }

        [TestMethod]
        public async Task OutcomeCannotCommitAfterStoredPlanChanges()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var plan = NormalizedPlan();
                var job = await AddJobAsync(repository, plan);
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
                using (var command = connection.CreateCommand())
                {
                    connection.Open();
                    command.CommandText = "UPDATE Revisions SET PlanJson = @Plan WHERE Id = @RevisionId;";
                    command.Parameters.AddWithValue("@Plan", plan.Replace("\"widthMm\":20", "\"widthMm\":21"));
                    command.Parameters.AddWithValue("@RevisionId", job.Item2.ToString("D"));
                    command.ExecuteNonQuery();
                }
                var plausible = CadCommandResult.Ok(new
                {
                    modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                });
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                        plausible, CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Pending,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task StartupRecoveryMarksPreparedAttemptUncertainWithoutAutomaticReplay()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan(), JobState.Executing);
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    var recovered = await reopened.FindV2MutationAttemptAsync(
                        job.Item1, job.Item2, "new", CancellationToken.None);
                    Assert.AreEqual(attempt.Id, recovered.Id);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain, recovered.Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await reopened.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                    var plausibleSuccess = CadCommandResult.Ok(new
                    {
                        modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                    });
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        reopened.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                            plausibleSuccess, CancellationToken.None));
                }
            });
        }

        [TestMethod]
        public async Task DispatchBoundaryCommitsOnlyItsOwnNewPartResultAndRejectsDuplicateDispatch()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(request => CadCommandResult.Ok(new
                {
                    modelId = request.Command.ManagedModelId.Value.ToString("D"),
                    documentTitle = "Part1",
                    configurationKey = "Default"
                }));
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);

                var result = await boundary.DispatchAndCommitAsync(
                    job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(2, executor.Request.PlanVersion);
                Assert.AreEqual(1, executor.Request.OperationVersion);
                Assert.AreEqual(CadCommandNames.NewPart, executor.Request.Command.Command);
                Assert.AreEqual(job.Item1, executor.Request.Command.ExecutionId);
                Assert.AreEqual(model.ModelId, executor.Request.Command.ManagedModelId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));
                Assert.AreEqual(1, executor.CallCount, "An Applied attempt must never be dispatched again.");
            });
        }

        [TestMethod]
        public async Task DispatchBoundaryRejectsStaleAttemptBeforeCallingExecutor()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => throw new AssertFailedException("Must not dispatch."));
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, Guid.NewGuid(), "new", attempt.Id, CancellationToken.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", Guid.NewGuid(), CancellationToken.None));

                Assert.AreEqual(0, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task DispatchBoundaryRejectsConcurrentDuplicateAcrossInstances()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new BlockingVersionedExecutor();
                var firstBoundary = new CadV2NewPartDispatchBoundary(repository, executor);
                var secondBoundary = new CadV2NewPartDispatchBoundary(repository, executor);
                var firstDispatch = firstBoundary.DispatchAndCommitAsync(
                    job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None);
                await executor.Started.Task;

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    secondBoundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));

                executor.Complete(CadCommandResult.Ok(new
                {
                    modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                }));
                await firstDispatch;
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task DispatchBoundaryMakesMissingOrMismatchedResultsUncertainAndDoesNotRetry()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => null);
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));

                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));
                Assert.AreEqual(1, executor.CallCount, "An uncertain native outcome must not be retried automatically.");
            });

            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new
                {
                    modelId = Guid.NewGuid().ToString("D"), documentTitle = "Part1", configurationKey = "Default"
                }));
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));

                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
            });

            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => throw new InvalidOperationException("Simulated ambiguous native failure."));
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));

                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
            });
        }

        [DataTestMethod]
        [DataRow("null")]
        [DataRow("throw")]
        [DataRow("commit")]
        public async Task DispatchBoundaryFencesAttemptWhenUncertainWriteFails(string failureMode)
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                ExecuteSql(path, "CREATE TRIGGER FailUncertain BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.Status = 'Uncertain' BEGIN SELECT RAISE(ABORT, 'forced uncertainty failure'); END;");
                if (failureMode == "commit")
                    ExecuteSql(path, "CREATE TRIGGER FailApplied BEFORE UPDATE ON V2MutationAttempts " +
                        "WHEN NEW.Status = 'Applied' BEGIN SELECT RAISE(ABORT, 'forced outcome failure'); END;");
                var nativeFailure = new InvalidOperationException("Ambiguous executor failure.");
                var executor = new RecordingVersionedExecutor(request =>
                {
                    if (failureMode == "throw") throw nativeFailure;
                    return failureMode == "null" ? null : CadCommandResult.Ok(new
                    {
                        modelId = request.Command.ManagedModelId.Value.ToString("D"),
                        documentTitle = "Part1", configurationKey = "Default"
                    });
                });
                var boundary = new CadV2NewPartDispatchBoundary(repository, executor);
                Exception observedFailure;
                if (failureMode == "commit")
                    observedFailure = await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() =>
                        boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));
                else
                    observedFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        boundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));

                if (failureMode == "throw") Assert.AreSame(nativeFailure, observedFailure);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Pending,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);

                var secondBoundary = new CadV2NewPartDispatchBoundary(repository, executor);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    secondBoundary.DispatchAndCommitAsync(job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));
                Assert.AreEqual(1, executor.CallCount,
                    "Failed uncertainty persistence must not release an ambiguously dispatched attempt.");
                Assert.IsInstanceOfType(observedFailure.Data["CadV2UncertainPersistenceFailure"],
                    typeof(System.Data.SQLite.SQLiteException), "The original error must retain the persistence failure diagnostic.");

                // Once storage is available, startup recovery closes the durable crash window.
                ExecuteSql(path, "DROP TRIGGER FailUncertain;");
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await reopened.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        new CadV2NewPartDispatchBoundary(reopened, executor).DispatchAndCommitAsync(
                            job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None));
                    Assert.AreEqual(1, executor.CallCount);
                    // Independently prove recovered state cannot authorize a commit, without the static fence.
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        reopened.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                            CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"),
                                documentTitle = "Part1", configurationKey = "Default" }), CancellationToken.None));
                }
            });
        }

        [TestMethod]
        public async Task DispatchBoundaryReleasesClaimAfterPreDispatchRejection()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var attempt = await repository.PrepareV2NewPartAttemptAsync(
                    job.Item1, job.Item2, "new", model, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(request => CadCommandResult.Ok(new
                {
                    modelId = request.Command.ManagedModelId.Value.ToString("D"),
                    documentTitle = "Part1", configurationKey = "Default"
                }));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    new CadV2NewPartDispatchBoundary(repository, executor).DispatchAndCommitAsync(
                        job.Item1, Guid.NewGuid(), "new", attempt.Id, CancellationToken.None));
                Assert.AreEqual(0, executor.CallCount);
                var result = await new CadV2NewPartDispatchBoundary(repository, executor).DispatchAndCommitAsync(
                    job.Item1, job.Item2, "new", attempt.Id, CancellationToken.None);
                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, executor.CallCount);
            });
        }

        private static void ExecuteSql(string path, string sql)
        {
            using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private sealed class RecordingVersionedExecutor : IVersionedCadCommandExecutor
        {
            private readonly Func<CadVersionedCommandRequest, CadCommandResult> _dispatch;
            public RecordingVersionedExecutor(Func<CadVersionedCommandRequest, CadCommandResult> dispatch) => _dispatch = dispatch;
            public int CallCount { get; private set; }
            public CadVersionedCommandRequest Request { get; private set; }
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken)
            {
                CallCount++;
                Request = request;
                return Task.FromResult(_dispatch(request));
            }
        }

        private sealed class BlockingVersionedExecutor : IVersionedCadCommandExecutor
        {
            private readonly TaskCompletionSource<CadCommandResult> _result =
                new TaskCompletionSource<CadCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Started { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CallCount { get; private set; }
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken)
            {
                CallCount++;
                Started.TrySetResult(true);
                return _result.Task;
            }
            public void Complete(CadCommandResult result) => _result.TrySetResult(result);
        }

        private static string NormalizedPlan() => JsonConvert.SerializeObject(
            CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(Candidate).CandidateV2));

        private static async Task<Tuple<Guid, Guid>> AddJobAsync(
            SqliteJobRepository repository, string plan, JobState state = JobState.Executing)
        {
            var now = DateTime.UtcNow;
            var jobId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            await repository.CreateAsync(new CadJob
            {
                Id = jobId, Prompt = "test", State = state, PlanValidated = true,
                CreatedUtc = now, UpdatedUtc = now
            });
            await repository.AppendRevisionAsync(new JobRevision
            {
                Id = revisionId, JobId = jobId, RevisionNumber = 1, Prompt = "test",
                PlanJson = plan, CreatedUtc = now
            });
            return Tuple.Create(jobId, revisionId);
        }

        private static CadModelIdentityRecord NewModel()
        {
            var now = DateTime.UtcNow;
            return new CadModelIdentityRecord
            {
                ModelId = Guid.NewGuid(), DocumentKind = "Part", Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId", CurrentModelRevisionId = Guid.NewGuid(),
                ConfigurationKey = "Default", RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now
            };
        }

        private static async Task WithRepositoryAsync(Func<string, SqliteJobRepository, Task> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "cad-v2-attempt-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                using (var repository = new SqliteJobRepository(path))
                {
                    await repository.InitializeAsync();
                    await action(path, repository);
                }
            }
            finally
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }
    }
}
