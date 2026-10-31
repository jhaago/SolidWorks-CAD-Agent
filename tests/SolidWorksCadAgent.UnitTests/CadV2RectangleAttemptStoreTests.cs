using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadV2RectangleAttemptStoreTests
    {
        private const string Candidate = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"rectangle\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}]}";
        private static string SaveCandidate => Candidate.Substring(0, Candidate.Length - 2) +
            ",{\"stepKey\":\"save\",\"command\":\"SavePart\",\"operationVersion\":1," +
            "\"parameters\":{\"path\":\"v2-owned.sldprt\",\"allowOverwrite\":false}}]}";

        [TestMethod]
        public async Task RectanglePreparationReservesExactNoOutputAttemptWithoutAdvancingModel()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);

                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, attempt.Status);
                Assert.AreEqual(ready.JobId, attempt.JobId);
                Assert.AreEqual(ready.RevisionId, attempt.RevisionId);
                Assert.AreEqual(ready.Model.ModelId, attempt.ModelId);
                Assert.IsNull(attempt.OutputEntityId, "AddRectangle does not produce a logical sketch identity.");
                Assert.AreEqual(ready.SketchAttempt.PlanSha256, attempt.PlanSha256);
                Assert.AreNotEqual(ready.SketchAttempt.ProspectiveModelRevisionId, attempt.ProspectiveModelRevisionId);
                Assert.AreEqual(ready.SketchAttempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId,
                    "Preparation must not claim unobserved geometry has changed the model.");
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);

                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    var persisted = await reopened.FindV2MutationAttemptAsync(
                        ready.JobId, ready.RevisionId, "rectangle", CancellationToken.None);
                    Assert.AreEqual(attempt.Id, persisted.Id);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, persisted.Status);
                    Assert.IsNull(persisted.OutputEntityId);
                }
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId));
            });
        }

        [TestMethod]
        public async Task RectanglePreparationRejectsUnappliedSketchWrongStepCrossJobAndStalePlan()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var pending = await ReadySketchAsync(repository, applySketch: false);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, pending.JobId, pending.RevisionId,
                        "rectangle", pending.Model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(
                    pending.JobId, pending.RevisionId, "rectangle", CancellationToken.None));

                await repository.CommitV2SketchOutcomeAsync(pending.JobId, pending.RevisionId, "sketch",
                    pending.Model.ModelId,
                    CadCommandResult.Ok(new { entityId = pending.SketchAttempt.OutputEntityId.Value.ToString("D") }),
                    CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, pending.JobId, pending.RevisionId,
                        "exit", pending.Model.ModelId));

                var otherJob = await AddJobAsync(repository);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, otherJob.Item1, otherJob.Item2,
                        "rectangle", pending.Model.ModelId));

                var changed = JObject.Parse(NormalizedPlan());
                changed["steps"][2]["parameters"]["widthMm"] = 24;
                UpdatePlanJson(path, pending.RevisionId, changed.ToString(Formatting.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, pending.JobId, pending.RevisionId,
                        "rectangle", pending.Model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(
                    pending.JobId, pending.RevisionId, "rectangle", CancellationToken.None));
            });

            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await repository.AppendRevisionAsync(new JobRevision
                {
                    Id = Guid.NewGuid(), JobId = ready.JobId, RevisionNumber = 2,
                    Prompt = "revised", PlanJson = NormalizedPlan(), CreatedUtc = DateTime.UtcNow
                });
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                        "rectangle", ready.Model.ModelId));
            });
        }

        [TestMethod]
        public async Task RectanglePreparationRequiresTheCommittedActiveSketchBinding()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                binding.Status = CadEntityReferenceStatus.Uncertain;
                binding.UpdatedUtc = DateTime.UtcNow;
                await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                        "rectangle", ready.Model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(
                    ready.JobId, ready.RevisionId, "rectangle", CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task RectangleReservationFailureLeavesCommittedSketchAndModelUntouched()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                ExecuteSql(path, "CREATE TRIGGER FailRectangle BEFORE INSERT ON V2MutationAttempts " +
                    "WHEN NEW.StepKey = 'rectangle' BEGIN SELECT RAISE(ABORT, 'forced rectangle reservation failure'); END;");
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                        "rectangle", ready.Model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(
                    ready.JobId, ready.RevisionId, "rectangle", CancellationToken.None));
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved, model.Status);
                Assert.AreEqual(ready.SketchAttempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
            });
        }

        [TestMethod]
        public async Task RectangleRecoveryMakesModelUncertainButRetainsCommittedSketchIdentity()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    var recovered = await reopened.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None);
                    var model = await reopened.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                    var binding = await reopened.GetEntityBindingAsync(ready.Model.ModelId,
                        ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain, recovered.Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                    Assert.AreEqual(ready.SketchAttempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status,
                        "The prior sketch identity was committed; its geometry outcome is guarded by the uncertain model.");
                    CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        PrepareRectangleAsync(reopened, ready.JobId, ready.RevisionId,
                            "rectangle", ready.Model.ModelId));
                }
            });
        }

        [TestMethod]
        public async Task RectangleDispatchCommitsOnlyTheObservedPreparedStepAndAdvancesModelOnce()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                var executor = new RecordingVersionedExecutor(request =>
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }));

                var result = await DispatchRectangleAsync(repository, executor, ready.JobId,
                    ready.RevisionId, "rectangle", attempt.Id);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(2, executor.Request.PlanVersion);
                Assert.AreEqual(1, executor.Request.OperationVersion);
                Assert.AreEqual("AddRectangle", executor.Request.Command.Command);
                Assert.AreEqual(ready.JobId, executor.Request.Command.ExecutionId);
                Assert.AreEqual(ready.Model.ModelId, executor.Request.Command.ManagedModelId);
                Assert.AreEqual(ready.SketchAttempt.OutputEntityId,
                    typeof(CadVersionedCommandRequest).GetProperty("TargetSketchEntityId")?.GetValue(executor.Request),
                    "The Host must bind the exact preceding sketch for native rectangle mutation.");
                Assert.IsNull(executor.Request.Command.OutputEntityId);
                Assert.AreEqual(20.0, executor.Request.Command.Parameters.Value<double>("widthMm"));
                Assert.AreEqual(10.0, executor.Request.Command.Parameters.Value<double>("heightMm"));
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(attempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved, model.Status);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId,
                        ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(1, executor.CallCount, "An Applied rectangle must never dispatch again.");
            });
        }

        [TestMethod]
        public async Task RectangleDispatchRejectsWrongIdentityAndChangedPlanBeforeExecutor()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                var executor = new RecordingVersionedExecutor(_ =>
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, Guid.NewGuid(), "rectangle", attempt.Id));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "exit", attempt.Id));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, Guid.NewGuid(), ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(0, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);

                var changed = JObject.Parse(NormalizedPlan());
                changed["steps"][2]["parameters"]["widthMm"] = 24;
                UpdatePlanJson(path, ready.RevisionId, changed.ToString(Formatting.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(0, executor.CallCount);
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).Status);
            });
        }

        [DataTestMethod]
        [DataRow("null")]
        [DataRow("failed")]
        [DataRow("mismatch")]
        [DataRow("throw")]
        public async Task RectangleDispatchMakesAmbiguousResultsUncertainWithoutReplaying(string mode)
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                var executor = new RecordingVersionedExecutor(_ =>
                {
                    if (mode == "throw") throw new InvalidOperationException("Native outcome unknown.");
                    if (mode == "null") return null;
                    if (mode == "failed") return new CadCommandResult { Success = false, Error = new CadError { Code = "RECTANGLE_FAILED" } };
                    return CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 21.0, heightMm = 10.0 });
                });
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                Assert.AreEqual(ready.SketchAttempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(1, executor.CallCount);
            });
        }

        [TestMethod]
        public async Task RectangleCommitFailureRollsBackRevisionBeforeBoundaryMarksUncertain()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                ExecuteSql(path, "CREATE TRIGGER FailAppliedRectangle BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.Status = 'Applied' AND NEW.StepKey = 'rectangle' BEGIN SELECT RAISE(ABORT, 'forced rectangle commit failure'); END;");
                var executor = new RecordingVersionedExecutor(_ =>
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }));
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                Assert.AreEqual(ready.SketchAttempt.ProspectiveModelRevisionId, model.CurrentModelRevisionId,
                    "The model revision update must roll back when the attempt update fails.");
            });
        }

        [TestMethod]
        public async Task RectangleDispatchRetainsProcessFenceWhenUncertainPersistenceFails()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                var attempt = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                ExecuteSql(path, "CREATE TRIGGER FailUncertainRectangle BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.Status = 'Uncertain' AND NEW.StepKey = 'rectangle' BEGIN SELECT RAISE(ABORT, 'forced uncertainty failure'); END;");
                var nativeFailure = new InvalidOperationException("Native outcome unknown.");
                var executor = new RecordingVersionedExecutor(_ => throw nativeFailure);
                var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreSame(nativeFailure, observed);
                Assert.IsInstanceOfType(observed.Data["CadV2UncertainPersistenceFailure"],
                    typeof(System.Data.SQLite.SQLiteException));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchRectangleAsync(repository, executor, ready.JobId, ready.RevisionId, "rectangle", attempt.Id));
                Assert.AreEqual(1, executor.CallCount, "Another boundary instance must not replay a possibly mutated rectangle.");

                ExecuteSql(path, "DROP TRIGGER FailUncertainRectangle;");
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await reopened.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).Status);
                    var binding = await reopened.GetEntityBindingAsync(ready.Model.ModelId,
                        ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                }
            });
        }

        [TestMethod]
        public async Task ExitSketchPreparationRequiresAppliedRectangleAndKeepsModelRevision()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                        "exit", ready.Model.ModelId, CancellationToken.None));
                var rectangle = await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }),
                    CancellationToken.None);

                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, exit.Status);
                Assert.IsNull(exit.OutputEntityId);
                Assert.AreEqual(rectangle.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                Assert.AreNotEqual(model.CurrentModelRevisionId, exit.ProspectiveModelRevisionId);
                Assert.AreEqual(rectangle.PlanSha256, exit.PlanSha256);
                var binding = await repository.GetEntityBindingAsync(ready.Model.ModelId,
                    ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                        "exit", ready.Model.ModelId, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task ExitSketchPreparationRejectsWrongStepAndRollsBackOnInsertFailure()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }),
                    CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                        "boss", ready.Model.ModelId, CancellationToken.None));
                ExecuteSql(path, "CREATE TRIGGER FailExitReservation BEFORE INSERT ON V2MutationAttempts " +
                    "WHEN NEW.StepKey = 'exit' BEGIN SELECT RAISE(ABORT, 'forced exit reservation failure'); END;");
                var inserted = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                        "exit", ready.Model.ModelId, CancellationToken.None));
                Assert.IsInstanceOfType(inserted.InnerException, typeof(System.Data.SQLite.SQLiteException));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task PreparedExitSketchRecoveryClosesModelButRetainsCommittedSketchEvidence()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId,
                    "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }),
                    CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var previousRevision = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(exit.Id, CancellationToken.None)).Status);
                    var model = await reopened.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                    Assert.AreEqual(previousRevision, model.CurrentModelRevisionId);
                    var binding = await reopened.GetEntityBindingAsync(ready.Model.ModelId,
                        ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                    CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
                }
            });
        }

        [TestMethod]
        public async Task ExitSketchDispatchBindsExactSketchAndCommitsObservedClosure()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new { exited = true }));
                var result = await DispatchExitAsync(repository, executor, ready.JobId, ready.RevisionId, "exit", exit.Id);
                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(ready.SketchAttempt.OutputEntityId, executor.Request.TargetSketchEntityId);
                Assert.AreEqual(CadCommandNames.ExitSketch, executor.Request.Command.Command);
                Assert.AreEqual(ready.JobId, executor.Request.Command.ExecutionId);
                Assert.AreEqual(ready.Model.ModelId, executor.Request.Command.ManagedModelId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(exit.Id, CancellationToken.None)).Status);
                Assert.AreEqual(exit.ProspectiveModelRevisionId,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchExitAsync(repository, executor, ready.JobId, ready.RevisionId, "exit", exit.Id));
                Assert.AreEqual(1, executor.CallCount);
            });
        }

        [TestMethod]
        public async Task ExitSketchDispatchRejectsFalseClosureAndMarksUncertain()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new { exited = false }));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchExitAsync(repository, executor, ready.JobId, ready.RevisionId, "exit", exit.Id));
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(exit.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task ExitSketchDispatchRejectsWrongAttemptBeforeNativeExecution()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new { exited = true }));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchExitAsync(repository, executor, ready.JobId, ready.RevisionId, "exit", Guid.NewGuid()));
                Assert.AreEqual(0, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(exit.Id, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task ExitSketchCommitRollsBackRevisionWhenAttemptUpdateFails()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                var priorRevision = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                ExecuteSql(path, "CREATE TRIGGER FailExitCommit BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.Status = 'Applied' AND NEW.StepKey = 'exit' BEGIN SELECT RAISE(ABORT, 'forced exit commit failure'); END;");
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() =>
                    repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                        CadCommandResult.Ok(new { exited = true }), CancellationToken.None));
                Assert.AreEqual(priorRevision,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(exit.Id, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task BossPreparationRequiresAppliedExitAndReservesNoOutputWithoutAdvancingModel()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                var exit = await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                        "boss", ready.Model.ModelId, CancellationToken.None));
                await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                    CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
                var boss = await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                    "boss", ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, boss.Status);
                Assert.IsNull(boss.OutputEntityId, "The current plan schema does not assign durable feature output IDs.");
                Assert.AreEqual(exit.ProspectiveModelRevisionId,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
                Assert.AreNotEqual(exit.ProspectiveModelRevisionId, boss.ProspectiveModelRevisionId);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                        "boss", ready.Model.ModelId, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task BossPreparationRejectsWrongStepAndPreservesReferenceOnRecovery()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                    CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                        "rectangle", ready.Model.ModelId, CancellationToken.None));
                var boss = await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                    "boss", ready.Model.ModelId, CancellationToken.None);
                var priorRevision = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(boss.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(priorRevision,
                        (await reopened.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
                    var binding = await reopened.GetEntityBindingAsync(ready.Model.ModelId,
                        ready.SketchAttempt.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                    CollectionAssert.AreEqual(ready.Token, binding.NativeReferenceBytes);
                }
            });
        }

        [TestMethod]
        public async Task BossDispatchBindsPlanOwnedSketchAndCommitsCorrelatedResult()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                    CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
                var boss = await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                    "boss", ready.Model.ModelId, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new { depthMm = 5.0, featureName = "Boss-Extrude1" }));
                var result = await DispatchBossAsync(repository, executor, ready.JobId, ready.RevisionId, "boss", boss.Id);
                Assert.IsTrue(result.Success);
                Assert.AreEqual(ready.SketchAttempt.OutputEntityId, executor.Request.ProfileSketchEntityId);
                Assert.AreEqual(2, executor.Request.OperationVersion);
                Assert.AreEqual(CadCommandNames.Extrude, executor.Request.Command.Command);
                Assert.AreEqual(ready.JobId, executor.Request.Command.ExecutionId);
                Assert.AreEqual(ready.Model.ModelId, executor.Request.Command.ManagedModelId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(boss.Id, CancellationToken.None)).Status);
                Assert.AreEqual(boss.ProspectiveModelRevisionId,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
            });
        }

        [TestMethod]
        public async Task BossDispatchRejectsWrongAttemptAndUncertainResultWithoutReplay()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                    CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
                var boss = await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                    "boss", ready.Model.ModelId, CancellationToken.None);
                var executor = new RecordingVersionedExecutor(_ => CadCommandResult.Ok(new { depthMm = 6.0, featureName = "Boss-Extrude1" }));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchBossAsync(repository, executor, ready.JobId, ready.RevisionId, "boss", Guid.NewGuid()));
                Assert.AreEqual(0, executor.CallCount);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchBossAsync(repository, executor, ready.JobId, ready.RevisionId, "boss", boss.Id));
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(boss.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    DispatchBossAsync(repository, executor, ready.JobId, ready.RevisionId, "boss", boss.Id));
                Assert.AreEqual(1, executor.CallCount);
            });
        }

        [TestMethod]
        public async Task BossCommitRollsBackModelRevisionOnAttemptWriteFailure()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository);
                await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
                await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                    CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
                await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                    "exit", ready.Model.ModelId, CancellationToken.None);
                await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                    CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
                var boss = await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                    "boss", ready.Model.ModelId, CancellationToken.None);
                var priorRevision = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                ExecuteSql(path, "CREATE TRIGGER FailBossCommit BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.Status = 'Applied' AND NEW.StepKey = 'boss' BEGIN SELECT RAISE(ABORT, 'forced boss commit failure'); END;");
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() =>
                    repository.CommitV2BossOutcomeAsync(ready.JobId, ready.RevisionId, "boss", ready.Model.ModelId,
                        CadCommandResult.Ok(new { depthMm = 5.0, featureName = "Boss-Extrude1" }), CancellationToken.None));
                Assert.AreEqual(priorRevision,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(boss.Id, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task RectangleBossVerificationChecksPlanGeometryAfterRebuild()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadyAppliedBossAsync(repository);
                var executor = new RecordingInspectionExecutor(command =>
                {
                    if (command.Command == CadCommandNames.GetRebuildErrors) return CadCommandResult.Ok(new { hasErrors = false });
                    if (command.Command == CadCommandNames.GetBodyCount) return CadCommandResult.Ok(new { bodyCount = 1, volumeMm3 = 1000.0 });
                    if (command.Command == CadCommandNames.GetPrismaticCutReadback) return CadCommandResult.Ok(new { cuts = new JArray() });
                    return CadCommandResult.Ok(new { minXmm = -10.0, maxXmm = 10.0, minYmm = 0.0, maxYmm = 5.0,
                        minZmm = -5.0, maxZmm = 5.0, sizeXmm = 20.0, sizeYmm = 5.0, sizeZmm = 10.0 });
                });
                var check = await new CadV2RectangleBossVerifier(repository, executor).VerifyAsync(
                    ready.JobId, ready.RevisionId, ready.Model.ModelId, CancellationToken.None);
                Assert.IsTrue(check.Passed, check.Reason);
                CollectionAssert.AreEqual(new[] { CadCommandNames.GetRebuildErrors, CadCommandNames.GetBodyCount,
                    CadCommandNames.GetBoundingBox, CadCommandNames.GetPrismaticCutReadback }, executor.Commands);
                Assert.IsTrue(executor.AllBoundTo(ready.JobId, ready.Model.ModelId));
            });
        }

        [TestMethod]
        public async Task RectangleBossVerificationRejectsWrongBoundsAndRebuildErrors()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadyAppliedBossAsync(repository);
                var wrong = new RecordingInspectionExecutor(command =>
                    command.Command == CadCommandNames.GetRebuildErrors ? CadCommandResult.Ok(new { hasErrors = false }) :
                    command.Command == CadCommandNames.GetBodyCount ? CadCommandResult.Ok(new { bodyCount = 1, volumeMm3 = 1000.0 }) :
                    CadCommandResult.Ok(new { minXmm = -12.0, maxXmm = 12.0, minYmm = 0.0, maxYmm = 5.0,
                        minZmm = -5.0, maxZmm = 5.0, sizeXmm = 24.0, sizeYmm = 5.0, sizeZmm = 10.0 }));
                var failed = await new CadV2RectangleBossVerifier(repository, wrong).VerifyAsync(
                    ready.JobId, ready.RevisionId, ready.Model.ModelId, CancellationToken.None);
                Assert.IsFalse(failed.Passed);
                Assert.IsTrue(failed.Reason.Contains("bounds"));
                var hollow = new RecordingInspectionExecutor(command =>
                    command.Command == CadCommandNames.GetRebuildErrors ? CadCommandResult.Ok(new { hasErrors = false }) :
                    command.Command == CadCommandNames.GetBodyCount ? CadCommandResult.Ok(new { bodyCount = 1, volumeMm3 = 900.0 }) :
                    CadCommandResult.Ok(new { minXmm = -10.0, maxXmm = 10.0, minYmm = 0.0, maxYmm = 5.0,
                        minZmm = -5.0, maxZmm = 5.0, sizeXmm = 20.0, sizeYmm = 5.0, sizeZmm = 10.0 }));
                failed = await new CadV2RectangleBossVerifier(repository, hollow).VerifyAsync(
                    ready.JobId, ready.RevisionId, ready.Model.ModelId, CancellationToken.None);
                Assert.IsFalse(failed.Passed);
                Assert.IsTrue(failed.Reason.Contains("volume"));
                Assert.IsTrue(failed.Reason.Contains("observed bounds"), "A volume mismatch should retain the available bounds for diagnosis.");
                CollectionAssert.AreEqual(new[] { CadCommandNames.GetRebuildErrors, CadCommandNames.GetBodyCount,
                    CadCommandNames.GetBoundingBox }, hollow.Commands);
                var rebuild = new RecordingInspectionExecutor(_ => CadCommandResult.Ok(new { hasErrors = true }));
                failed = await new CadV2RectangleBossVerifier(repository, rebuild).VerifyAsync(
                    ready.JobId, ready.RevisionId, ready.Model.ModelId, CancellationToken.None);
                Assert.IsFalse(failed.Passed);
                Assert.AreEqual(1, rebuild.Commands.Length, "Failed rebuild must stop before later inspection.");
                var missing = new RecordingInspectionExecutor(_ => null);
                failed = await new CadV2RectangleBossVerifier(repository, missing).VerifyAsync(
                    ready.JobId, ready.RevisionId, ready.Model.ModelId, CancellationToken.None);
                Assert.IsFalse(failed.Passed);
                Assert.AreEqual(1, missing.Commands.Length);
            });
        }

        [TestMethod]
        public async Task SavePreparationRequiresAppliedBossAndReservesStateRevision()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                        "save", ready.Model.ModelId, CancellationToken.None));
                await ApplyBossAsync(repository, ready);
                var priorRevision = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, save.Status);
                Assert.AreNotEqual(priorRevision, save.ProspectiveModelRevisionId,
                    "Save needs a unique managed state revision; its geometry remains unchanged.");
                Assert.AreEqual(priorRevision,
                    (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId,
                    "Reservation alone must not advance the committed state revision.");
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                        "save", ready.Model.ModelId, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task SavePreparationRejectsOverwriteAndRecoversUnresolvedAttempt()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                        "boss", ready.Model.ModelId, CancellationToken.None));
                var altered = JObject.Parse(NormalizedPlan(includeSave: true));
                altered["steps"][5]["parameters"]["allowOverwrite"] = true;
                UpdatePlanJson(path, ready.RevisionId, altered.ToString(Formatting.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                        "save", ready.Model.ModelId, CancellationToken.None));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(
                    ready.JobId, ready.RevisionId, "save", CancellationToken.None));
            });

            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                var prior = (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).CurrentModelRevisionId;
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(save.Id, CancellationToken.None)).Status);
                    var model = await reopened.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain, model.Status);
                    Assert.AreEqual(prior, model.CurrentModelRevisionId);
                    Assert.IsNull(model.CanonicalPath);
                }
            });
        }

        [TestMethod]
        public async Task SaveDispatchVerifiesGeometryAndAtomicallyCommitsExactArtifact()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                var workspace = Path.Combine(Path.GetTempPath(), "cad-v2-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                try
                {
                    var executor = new RecordingSaveExecutor(workspace);
                    var result = await DispatchSaveAsync(repository, executor, new WorkspacePolicy(workspace),
                        ready.JobId, ready.RevisionId, "save", save.Id);
                    Assert.IsTrue(result.Success);
                    Assert.AreEqual(1, executor.SaveCount);
                    Assert.AreEqual(CadCommandNames.GetRebuildErrors, executor.Commands[0]);
                    Assert.AreEqual(CadCommandNames.SavePart, executor.Commands[4]);
                    Assert.AreEqual(ready.JobId, executor.SaveRequest.Command.ExecutionId);
                    Assert.AreEqual(ready.Model.ModelId, executor.SaveRequest.Command.ManagedModelId);
                    Assert.AreEqual(false, executor.SaveRequest.Command.Parameters.Value<bool>("allowOverwrite"));
                    var model = await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.ActiveSaved, model.Status);
                    Assert.AreEqual(save.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                    Assert.AreEqual(Path.Combine(workspace, "v2-owned.sldprt"), model.CanonicalPath);
                    Assert.AreEqual(64, model.LastSavedSha256.Length);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                        (await repository.GetV2MutationAttemptAsync(save.Id, CancellationToken.None)).Status);
                    var verifier = new CadV2RectangleBossVerifier(repository, executor);
                    var savedCheck = await verifier.VerifySavedAsync(ready.JobId, ready.RevisionId,
                        ready.Model.ModelId, new WorkspacePolicy(workspace), CancellationToken.None);
                    Assert.IsTrue(savedCheck.Passed, savedCheck.Reason);
                    var recorder = new CadV2SavedVerificationRecorder(repository, executor, new WorkspacePolicy(workspace));
                    var stage = CadV2SavePath.StageFor(model.CanonicalPath, ready.Model.ModelId);
                    using (var lockedStage = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        Assert.IsTrue(await recorder.VerifyAndRecordAsync(ready.JobId, ready.RevisionId,
                            ready.Model.ModelId, CancellationToken.None),
                            "An undeletable staging copy must not block readback of the finalized part.");
                    var passing = (await repository.GetSnapshotAsync(ready.JobId, CancellationToken.None)).Verifications.Last();
                    Assert.AreEqual(CadV2SavedVerificationRecorder.CheckName, passing.CheckName);
                    Assert.IsTrue(passing.Passed);
                    Assert.AreEqual(model.LastSavedSha256, JObject.Parse(passing.ExpectedJson).Value<string>("sha256"));
                    Assert.AreEqual(model.LastSavedSha256, JObject.Parse(passing.ActualJson).Value<string>("sha256"));
                    Assert.IsNotNull(JObject.Parse(passing.ActualJson)["observed"]["bounds"]);
                    Assert.AreEqual(CadCommandNames.CloseDocument, executor.Commands[9]);
                    Assert.AreEqual(CadCommandNames.OpenPart, executor.Commands[10]);
                    Assert.AreEqual(CadCommandNames.GetRebuildErrors, executor.Commands[11]);
                    var observations = executor.Commands.Length;
                    File.AppendAllText(model.CanonicalPath, "tampered");
                    var changed = await verifier.VerifySavedAsync(ready.JobId, ready.RevisionId,
                        ready.Model.ModelId, new WorkspacePolicy(workspace), CancellationToken.None);
                    Assert.IsFalse(changed.Passed);
                    Assert.IsTrue(changed.Reason.Contains("hash"));
                    Assert.AreEqual(observations, executor.Commands.Length,
                        "A mismatched artifact must be rejected before CAD observation.");
                    Assert.IsFalse(await recorder.VerifyAndRecordAsync(ready.JobId, ready.RevisionId,
                        ready.Model.ModelId, CancellationToken.None));
                    var failing = (await repository.GetSnapshotAsync(ready.JobId, CancellationToken.None)).Verifications.Last();
                    Assert.IsFalse(failing.Passed);
                    Assert.AreEqual(CadV2SavedVerificationRecorder.CheckName, failing.CheckName);
                    File.Delete(model.CanonicalPath);
                    Assert.IsFalse(await recorder.VerifyAndRecordAsync(ready.JobId, ready.RevisionId,
                        ready.Model.ModelId, CancellationToken.None));
                    var missing = (await repository.GetSnapshotAsync(ready.JobId, CancellationToken.None)).Verifications.Last();
                    Assert.IsFalse(missing.Passed);
                    Assert.IsTrue(JObject.Parse(missing.ActualJson).Value<string>("reason").Contains("unavailable"));
                }
                finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
            });
        }

        [TestMethod]
        public async Task SaveDispatchRejectsExistingTargetBeforeNativeCall()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                var workspace = Path.Combine(Path.GetTempPath(), "cad-v2-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                try
                {
                    var target = Path.Combine(workspace, "v2-owned.sldprt");
                    File.WriteAllText(target, "original file");
                    var executor = new RecordingSaveExecutor(workspace);
                    await Assert.ThrowsExceptionAsync<WorkspacePolicyException>(() => DispatchSaveAsync(
                        repository, executor, new WorkspacePolicy(workspace),
                        ready.JobId, ready.RevisionId, "save", save.Id));
                    Assert.AreEqual(0, executor.SaveCount);
                    Assert.AreEqual("original file", File.ReadAllText(target));
                    Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                        (await repository.GetV2MutationAttemptAsync(save.Id, CancellationToken.None)).Status);
                }
                finally { Directory.Delete(workspace, true); }
            });
        }

        [TestMethod]
        public async Task SaveDispatchTreatsMismatchedNativePathAsUncertainAndBlocksReplay()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                var workspace = Path.Combine(Path.GetTempPath(), "cad-v2-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                try
                {
                    var executor = new RecordingSaveExecutor(workspace) { ReturnWrongPath = true };
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => DispatchSaveAsync(
                        repository, executor, new WorkspacePolicy(workspace),
                        ready.JobId, ready.RevisionId, "save", save.Id));
                    Assert.AreEqual(1, executor.SaveCount);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await repository.GetV2MutationAttemptAsync(save.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await repository.GetModelAsync(ready.Model.ModelId, CancellationToken.None)).Status);
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => DispatchSaveAsync(
                        repository, executor, new WorkspacePolicy(workspace),
                        ready.JobId, ready.RevisionId, "save", save.Id));
                    Assert.AreEqual(1, executor.SaveCount, "An uncertain save cannot be replayed.");
                }
                finally { Directory.Delete(workspace, true); }
            });
        }

        [TestMethod]
        public async Task SavedVerificationRecordsFailureWhenManagedReopenFails()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var ready = await ReadySketchAsync(repository, includeSave: true);
                await ApplyBossAsync(repository, ready);
                var save = await repository.PrepareV2SaveAttemptAsync(ready.JobId, ready.RevisionId,
                    "save", ready.Model.ModelId, CancellationToken.None);
                var workspace = Path.Combine(Path.GetTempPath(), "cad-v2-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                try
                {
                    var executor = new RecordingSaveExecutor(workspace) { FailOpen = true };
                    await DispatchSaveAsync(repository, executor, new WorkspacePolicy(workspace),
                        ready.JobId, ready.RevisionId, "save", save.Id);
                    var observations = executor.Commands.Length;
                    var recorder = new CadV2SavedVerificationRecorder(repository, executor, new WorkspacePolicy(workspace));
                    Assert.IsFalse(await recorder.VerifyAndRecordAsync(ready.JobId, ready.RevisionId,
                        ready.Model.ModelId, CancellationToken.None));
                    Assert.AreEqual(CadCommandNames.CloseDocument, executor.Commands[observations]);
                    Assert.AreEqual(CadCommandNames.OpenPart, executor.Commands[observations + 1]);
                    Assert.AreEqual(observations + 2, executor.Commands.Length,
                        "A failed managed reopen must not produce geometry observations.");
                    Assert.IsFalse((await repository.GetSnapshotAsync(ready.JobId, CancellationToken.None)).Verifications.Last().Passed);
                }
                finally { Directory.Delete(workspace, true); }
            });
        }

        private static async Task<CadCommandResult> DispatchSaveAsync(SqliteJobRepository repository,
            IVersionedCadCommandExecutor executor, WorkspacePolicy workspace, Guid jobId, Guid revisionId,
            string stepKey, Guid attemptId)
        {
            var type = typeof(SqliteJobRepository).Assembly.GetType("SolidWorksCadAgent.AgentHost.Jobs.CadV2SaveDispatchBoundary");
            Assert.IsNotNull(type, "The Host must provide an isolated final-save dispatch boundary.");
            var boundary = Activator.CreateInstance(type, repository, executor, workspace);
            try
            {
                return await (Task<CadCommandResult>)type.GetMethod("DispatchAndCommitAsync").Invoke(boundary,
                    new object[] { jobId, revisionId, stepKey, attemptId, CancellationToken.None });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private sealed class RecordingSaveExecutor : ICadCommandExecutor, IVersionedCadCommandExecutor
        {
            private readonly string _workspace;
            private readonly System.Collections.Generic.List<string> _commands = new System.Collections.Generic.List<string>();
            public RecordingSaveExecutor(string workspace) => _workspace = workspace;
            public string[] Commands => _commands.ToArray();
            public int SaveCount { get; private set; }
            public CadVersionedCommandRequest SaveRequest { get; private set; }
            public bool ReturnWrongPath { get; set; }
            public bool FailOpen { get; set; }
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            {
                _commands.Add(command.Command);
                if (command.Command == CadCommandNames.GetRebuildErrors) return Task.FromResult(CadCommandResult.Ok(new { hasErrors = false }));
                if (command.Command == CadCommandNames.GetBodyCount) return Task.FromResult(CadCommandResult.Ok(new { bodyCount = 1, volumeMm3 = 1000.0 }));
                if (command.Command == CadCommandNames.GetBoundingBox) return Task.FromResult(CadCommandResult.Ok(new
                {
                    minXmm = -10.0, maxXmm = 10.0, minYmm = 0.0, maxYmm = 5.0,
                    minZmm = -5.0, maxZmm = 5.0, sizeXmm = 20.0, sizeYmm = 5.0, sizeZmm = 10.0
                }));
                if (command.Command == CadCommandNames.GetPrismaticCutReadback)
                    return Task.FromResult(CadCommandResult.Ok(new { cuts = new JArray() }));
                if (command.Command == CadCommandNames.CloseDocument)
                    return Task.FromResult(CadCommandResult.Ok(new { documentTitle = "v2-owned.sldprt", closed = true }));
                if (command.Command == CadCommandNames.OpenPart)
                    if (FailOpen) return Task.FromResult(new CadCommandResult
                    {
                        Success = false, Data = new JObject(),
                        Error = new CadError { Code = "OPEN_FAILED", Message = "fixture open failed" }
                    });
                if (command.Command == CadCommandNames.OpenPart)
                    return Task.FromResult(CadCommandResult.Ok(new
                    {
                        path = new WorkspacePolicy(_workspace).ResolveForRead(command.Parameters.Value<string>("path")),
                        documentTitle = "v2-owned.sldprt", errors = 0, warnings = 0
                    }));
                if (command.Command == CadCommandNames.SavePart)
                {
                    SaveCount++;
                    var output = new WorkspacePolicy(_workspace).ResolveForWrite(command.Parameters.Value<string>("path"), false);
                    var stage = CadV2SavePath.StageFor(output, command.ManagedModelId.Value);
                    File.WriteAllText(stage, "owned fixture artifact");
                    File.Copy(stage, output, false);
                    return Task.FromResult(CadCommandResult.Ok(new {
                        path = ReturnWrongPath ? Path.Combine(_workspace, "different.sldprt") : output,
                        errors = 0, warnings = 0, stagingPath = stage }));
                }
                throw new InvalidOperationException("Unexpected command " + command.Command);
            }
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken token)
            {
                SaveRequest = request;
                return ExecuteAsync(request.Command, token);
            }
        }

        private static async Task<ReadySketch> ReadyAppliedBossAsync(SqliteJobRepository repository)
        {
            var ready = await ReadySketchAsync(repository);
            await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
            await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
            await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                "exit", ready.Model.ModelId, CancellationToken.None);
            await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
            await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                "boss", ready.Model.ModelId, CancellationToken.None);
            await repository.CommitV2BossOutcomeAsync(ready.JobId, ready.RevisionId, "boss", ready.Model.ModelId,
                CadCommandResult.Ok(new { depthMm = 5.0, featureName = "Boss-Extrude1" }), CancellationToken.None);
            return ready;
        }

        private static async Task ApplyBossAsync(SqliteJobRepository repository, ReadySketch ready)
        {
            await PrepareRectangleAsync(repository, ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId);
            await repository.CommitV2RectangleOutcomeAsync(ready.JobId, ready.RevisionId, "rectangle", ready.Model.ModelId,
                CadCommandResult.Ok(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }), CancellationToken.None);
            await repository.PrepareV2ExitSketchAttemptAsync(ready.JobId, ready.RevisionId,
                "exit", ready.Model.ModelId, CancellationToken.None);
            await repository.CommitV2ExitSketchOutcomeAsync(ready.JobId, ready.RevisionId, "exit", ready.Model.ModelId,
                CadCommandResult.Ok(new { exited = true }), CancellationToken.None);
            await repository.PrepareV2BossAttemptAsync(ready.JobId, ready.RevisionId,
                "boss", ready.Model.ModelId, CancellationToken.None);
            await repository.CommitV2BossOutcomeAsync(ready.JobId, ready.RevisionId, "boss", ready.Model.ModelId,
                CadCommandResult.Ok(new { depthMm = 5.0, featureName = "Boss-Extrude1" }), CancellationToken.None);
        }

        private sealed class RecordingInspectionExecutor : ICadCommandExecutor
        {
            private readonly Func<CadCommandEnvelope, CadCommandResult> _dispatch;
            private readonly System.Collections.Generic.List<CadCommandEnvelope> _commands = new System.Collections.Generic.List<CadCommandEnvelope>();
            public RecordingInspectionExecutor(Func<CadCommandEnvelope, CadCommandResult> dispatch) => _dispatch = dispatch;
            public string[] Commands => _commands.ConvertAll(command => command.Command).ToArray();
            public bool AllBoundTo(Guid jobId, Guid modelId) => _commands.TrueForAll(command =>
                command.ExecutionId == jobId && command.ManagedModelId == modelId);
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            {
                _commands.Add(command);
                return Task.FromResult(_dispatch(command));
            }
        }

        private static async Task<CadCommandResult> DispatchBossAsync(SqliteJobRepository repository,
            IVersionedCadCommandExecutor executor, Guid jobId, Guid revisionId, string stepKey, Guid attemptId)
        {
            var type = typeof(SqliteJobRepository).Assembly.GetType("SolidWorksCadAgent.AgentHost.Jobs.CadV2BossDispatchBoundary");
            Assert.IsNotNull(type, "The Host must provide an isolated boss dispatch boundary.");
            var boundary = Activator.CreateInstance(type, repository, executor);
            try
            {
                return await (Task<CadCommandResult>)type.GetMethod("DispatchAndCommitAsync").Invoke(boundary,
                    new object[] { jobId, revisionId, stepKey, attemptId, CancellationToken.None });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static async Task<CadCommandResult> DispatchExitAsync(SqliteJobRepository repository,
            IVersionedCadCommandExecutor executor, Guid jobId, Guid revisionId, string stepKey, Guid attemptId)
        {
            var type = typeof(SqliteJobRepository).Assembly.GetType("SolidWorksCadAgent.AgentHost.Jobs.CadV2ExitSketchDispatchBoundary");
            Assert.IsNotNull(type, "The Host must provide an isolated ExitSketch dispatch boundary.");
            var boundary = Activator.CreateInstance(type, repository, executor);
            try
            {
                return await (Task<CadCommandResult>)type.GetMethod("DispatchAndCommitAsync").Invoke(boundary,
                    new object[] { jobId, revisionId, stepKey, attemptId, CancellationToken.None });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static async Task<CadCommandResult> DispatchRectangleAsync(SqliteJobRepository repository,
            IVersionedCadCommandExecutor executor, Guid jobId, Guid revisionId, string stepKey, Guid attemptId)
        {
            var type = typeof(SqliteJobRepository).Assembly.GetType(
                "SolidWorksCadAgent.AgentHost.Jobs.CadV2RectangleDispatchBoundary");
            Assert.IsNotNull(type, "The Host must provide an isolated rectangle dispatch boundary.");
            var boundary = Activator.CreateInstance(type, repository, executor);
            try
            {
                return await (Task<CadCommandResult>)type.GetMethod("DispatchAndCommitAsync").Invoke(boundary,
                    new object[] { jobId, revisionId, stepKey, attemptId, CancellationToken.None });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private sealed class RecordingVersionedExecutor : IVersionedCadCommandExecutor
        {
            private readonly Func<CadVersionedCommandRequest, CadCommandResult> _dispatch;
            public RecordingVersionedExecutor(Func<CadVersionedCommandRequest, CadCommandResult> dispatch) => _dispatch = dispatch;
            public int CallCount { get; private set; }
            public CadVersionedCommandRequest Request { get; private set; }
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken token)
            {
                CallCount++;
                Request = request;
                return Task.FromResult(_dispatch(request));
            }
        }

        private static async Task<CadV2MutationAttemptRecord> PrepareRectangleAsync(
            SqliteJobRepository repository, Guid jobId, Guid revisionId, string stepKey, Guid modelId)
        {
            return await repository.PrepareV2RectangleAttemptAsync(
                jobId, revisionId, stepKey, modelId, CancellationToken.None);
        }

        private sealed class ReadySketch
        {
            public Guid JobId { get; set; }
            public Guid RevisionId { get; set; }
            public CadModelIdentityRecord Model { get; set; }
            public CadV2MutationAttemptRecord SketchAttempt { get; set; }
            public byte[] Token { get; set; }
        }

        private static async Task<ReadySketch> ReadySketchAsync(
            SqliteJobRepository repository, bool applySketch = true, bool includeSave = false)
        {
            var job = await AddJobAsync(repository, includeSave);
            var model = NewModel();
            await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
            await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                CancellationToken.None);
            var sketch = await repository.PrepareV2SketchAttemptAsync(
                job.Item1, job.Item2, "sketch", model.ModelId, CancellationToken.None);
            var token = new byte[] { 4, 5, 6 };
            var binding = await repository.GetEntityBindingAsync(model.ModelId,
                sketch.OutputEntityId.Value, "Default", CancellationToken.None);
            binding.NativeReferenceBytes = (byte[])token.Clone();
            binding.Status = CadEntityReferenceStatus.Active;
            binding.UpdatedUtc = DateTime.UtcNow;
            await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
            if (applySketch)
                await repository.CommitV2SketchOutcomeAsync(job.Item1, job.Item2, "sketch", model.ModelId,
                    CadCommandResult.Ok(new { entityId = sketch.OutputEntityId.Value.ToString("D") }), CancellationToken.None);
            return new ReadySketch
            {
                JobId = job.Item1, RevisionId = job.Item2, Model = model,
                SketchAttempt = sketch, Token = token
            };
        }

        private static string NormalizedPlan(bool includeSave = false) => JsonConvert.SerializeObject(
            CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(includeSave ? SaveCandidate : Candidate).CandidateV2));

        private static async Task<Tuple<Guid, Guid>> AddJobAsync(SqliteJobRepository repository, bool includeSave = false)
        {
            var now = DateTime.UtcNow;
            var jobId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            await repository.CreateAsync(new CadJob
            {
                Id = jobId, Prompt = "rectangle reservation", State = JobState.Executing,
                PlanValidated = true, CreatedUtc = now, UpdatedUtc = now
            });
            await repository.AppendRevisionAsync(new JobRevision
            {
                Id = revisionId, JobId = jobId, RevisionNumber = 1,
                Prompt = "rectangle reservation", PlanJson = NormalizedPlan(includeSave), CreatedUtc = now
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

        private static void UpdatePlanJson(string path, Guid revisionId, string planJson)
        {
            using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "UPDATE Revisions SET PlanJson = @PlanJson WHERE Id = @Id;";
                command.Parameters.AddWithValue("@PlanJson", planJson);
                command.Parameters.AddWithValue("@Id", revisionId.ToString("D"));
                Assert.AreEqual(1, command.ExecuteNonQuery());
            }
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

        private static async Task WithRepositoryAsync(Func<string, SqliteJobRepository, Task> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "cad-v2-rectangle-" + Guid.NewGuid().ToString("N") + ".sqlite");
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
