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
        public async Task SketchAttemptAndPendingBindingArePreparedAtomicallyForAppliedOwnedModel()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var newPart = await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var snapshot = await repository.GetSnapshotAsync(job.Item1, CancellationToken.None);
                var sketchId = CadPlanDocumentReader.ReadPersisted(snapshot.Revisions[0].PlanJson).CandidateV2.Steps[1].OutputEntityId.Value;

                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                var binding = await repository.GetEntityBindingAsync(model.ModelId, sketchId, "Default", CancellationToken.None);
                var currentModel = await repository.GetModelAsync(model.ModelId, CancellationToken.None);
                var appliedNewPart = await repository.GetV2MutationAttemptAsync(newPart.Id, CancellationToken.None);

                Assert.AreEqual(CadV2MutationAttemptStatus.Applied, appliedNewPart.Status);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, attempt.Status);
                Assert.AreEqual(sketchId, attempt.OutputEntityId.Value);
                Assert.AreEqual(CadEntityReferenceStatus.Pending, binding.Status);
                Assert.IsNull(binding.NativeReferenceBytes);
                Assert.AreEqual("Sketch", binding.EntityKind);
                Assert.AreEqual("SketchFeature", binding.NativeObjectKind);
                Assert.AreEqual(3, binding.ReferenceFormatVersion);
                Assert.AreEqual(attempt.ProspectiveModelRevisionId, binding.CreatedAtModelRevisionId);
                Assert.AreEqual(model.CurrentModelRevisionId, currentModel.CurrentModelRevisionId,
                    "Preparation must not claim the native sketch mutation has advanced the model.");
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved, currentModel.Status);
            });
        }

        [TestMethod]
        public async Task SketchAttemptRejectsBeforeNewPartOutcomeIsApplied()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(job.Item1, job.Item2, "sketch", CancellationToken.None));
                var snapshot = await repository.GetSnapshotAsync(job.Item1, CancellationToken.None);
                Assert.IsNull(await repository.GetEntityBindingAsync(model.ModelId,
                    CadPlanDocumentReader.ReadPersisted(snapshot.Revisions[0].PlanJson).CandidateV2.Steps[1].OutputEntityId.Value,
                    "Default", CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task SketchAttemptRejectsWrongStepCrossJobModelAndDuplicate()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var firstJob = await AddJobAsync(repository, NormalizedPlan());
                var firstModel = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(firstJob.Item1, firstJob.Item2, "new", firstModel, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(firstJob.Item1, firstJob.Item2, "new", firstModel.ModelId,
                    CadCommandResult.Ok(new { modelId = firstModel.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var secondJob = await AddJobAsync(repository, NormalizedPlan());
                var secondModel = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(secondJob.Item1, secondJob.Item2, "new", secondModel, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(secondJob.Item1, secondJob.Item2, "new", secondModel.ModelId,
                    CadCommandResult.Ok(new { modelId = secondModel.ModelId.ToString("D"), documentTitle = "Part2", configurationKey = "Default" }),
                    CancellationToken.None);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareSketchAsync(repository, firstJob.Item1, firstJob.Item2, "wrong-step", firstModel.ModelId));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareSketchAsync(repository, firstJob.Item1, firstJob.Item2, "sketch", secondModel.ModelId));

                var prepared = await PrepareSketchAsync(repository, firstJob.Item1, firstJob.Item2, "sketch", firstModel.ModelId);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareSketchAsync(repository, firstJob.Item1, firstJob.Item2, "sketch", firstModel.ModelId));
                var stored = await repository.GetV2MutationAttemptAsync(prepared.Id, CancellationToken.None);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared, stored.Status);
                Assert.AreEqual(firstJob.Item1, stored.JobId);
                Assert.AreEqual(firstModel.ModelId, stored.ModelId);
            });
        }

        [TestMethod]
        public async Task FailedPendingBindingInsertRollsBackSketchAttempt()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                ExecuteSql(path, "CREATE TRIGGER FailPendingBinding BEFORE INSERT ON EntityReferenceBindings " +
                    "WHEN NEW.Status = 'Pending' BEGIN SELECT RAISE(ABORT, 'forced pending binding failure'); END;");

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId));
                Assert.IsNull(await repository.FindV2MutationAttemptAsync(job.Item1, job.Item2, "sketch", CancellationToken.None));
                var snapshot = await repository.GetSnapshotAsync(job.Item1, CancellationToken.None);
                Assert.IsNull(await repository.GetEntityBindingAsync(model.ModelId,
                    CadPlanDocumentReader.ReadPersisted(snapshot.Revisions[0].PlanJson).CandidateV2.Steps[1].OutputEntityId.Value,
                    "Default", CancellationToken.None));
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
            });
        }

        [TestMethod]
        public async Task StartupRecoveryMarksPreparedSketchBindingUncertainAtomically()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    var recoveredAttempt = await reopened.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None);
                    var recoveredBinding = await reopened.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value,
                        "Default", CancellationToken.None);
                    var recoveredModel = await reopened.GetModelAsync(model.ModelId, CancellationToken.None);
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain, recoveredAttempt.Status);
                    Assert.AreEqual(CadEntityReferenceStatus.Uncertain, recoveredBinding.Status);
                    Assert.AreEqual(attempt.ProspectiveModelRevisionId, recoveredBinding.CreatedAtModelRevisionId);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain, recoveredModel.Status);
                }
            });
        }

        private static async Task<CadV2MutationAttemptRecord> PrepareSketchAsync(
            SqliteJobRepository repository, Guid jobId, Guid revisionId, string stepKey, Guid modelId)
        {
            var method = typeof(SqliteJobRepository).GetMethod("PrepareV2SketchAttemptAsync");
            Assert.IsNotNull(method, "The repository must expose atomic v2 sketch-attempt preparation.");
            try
            {
                return await (Task<CadV2MutationAttemptRecord>)method.Invoke(repository,
                    new object[] { jobId, revisionId, stepKey, modelId, CancellationToken.None });
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
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
        public async Task StartupRecoveryMarksBridgeActivatedSketchBindingUncertainBeforeHostCommit()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                var binding = await repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None);
                binding.NativeReferenceBytes = new byte[] { 1, 2, 3 };
                binding.Status = CadEntityReferenceStatus.Active;
                binding.UpdatedUtc = DateTime.UtcNow;
                await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);

                using (var reopened = new SqliteJobRepository(path))
                {
                    await reopened.InitializeAsync();
                    await reopened.RecoverInterruptedJobsAsync();
                    Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                        (await reopened.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                    Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                        (await reopened.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                    var recoveredBinding = await reopened.GetEntityBindingAsync(
                        model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Uncertain, recoveredBinding.Status,
                        "A Bridge-persisted active token must not outlive an uncommitted Host outcome.");
                }
            });
        }

        [TestMethod]
        public async Task SketchDispatchCorrelatesBridgeReferenceAndAtomicallyAppliesExactAttempt()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                var executor = new RecordingVersionedExecutor(request =>
                {
                    Assert.AreEqual(2, request.PlanVersion);
                    Assert.AreEqual(1, request.OperationVersion);
                    Assert.AreEqual(CadCommandNames.CreateSketch, request.Command.Command);
                    Assert.AreEqual(job.Item1, request.Command.ExecutionId);
                    Assert.AreEqual(model.ModelId, request.Command.ManagedModelId);
                    Assert.AreEqual(attempt.OutputEntityId.Value, request.Command.OutputEntityId);
                    Assert.AreEqual("Top Plane", request.Command.Parameters.Value<string>("plane"));
                    var nativeBinding = repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value,
                        "Default", CancellationToken.None).GetAwaiter().GetResult();
                    nativeBinding.NativeReferenceBytes = new byte[] { 9, 8, 7 };
                    nativeBinding.Status = CadEntityReferenceStatus.Active;
                    nativeBinding.UpdatedUtc = DateTime.UtcNow;
                    repository.UpdateEntityBindingAsync(nativeBinding, CancellationToken.None).GetAwaiter().GetResult();
                    return CadCommandResult.Ok(new { entityId = attempt.OutputEntityId.Value.ToString("D") });
                });

                var result = await new CadV2CreateSketchDispatchBoundary(repository, executor).DispatchAndCommitAsync(
                    job.Item1, job.Item2, "sketch", attempt.Id, CancellationToken.None);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                var updatedModel = await repository.GetModelAsync(model.ModelId, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.ActiveUnsaved, updatedModel.Status);
                Assert.AreEqual(attempt.ProspectiveModelRevisionId, updatedModel.CurrentModelRevisionId);
                var activeBinding = await repository.GetEntityBindingAsync(
                    model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None);
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, activeBinding.NativeReferenceBytes);
                Assert.AreEqual(CadEntityReferenceStatus.Active, activeBinding.Status);
            });
        }

        [TestMethod]
        public async Task SketchDispatchMismatchMakesAttemptModelAndActivatedBindingUncertainWithoutReplay()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                var executor = new RecordingVersionedExecutor(_ =>
                {
                    var binding = repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value,
                        "Default", CancellationToken.None).GetAwaiter().GetResult();
                    binding.NativeReferenceBytes = new byte[] { 4, 5, 6 };
                    binding.Status = CadEntityReferenceStatus.Active;
                    binding.UpdatedUtc = DateTime.UtcNow;
                    repository.UpdateEntityBindingAsync(binding, CancellationToken.None).GetAwaiter().GetResult();
                    return CadCommandResult.Ok(new { entityId = Guid.NewGuid().ToString("D") });
                });
                var boundary = new CadV2CreateSketchDispatchBoundary(repository, executor);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => boundary.DispatchAndCommitAsync(
                    job.Item1, job.Item2, "sketch", attempt.Id, CancellationToken.None));

                Assert.AreEqual(1, executor.CallCount);
                Assert.AreEqual(CadV2MutationAttemptStatus.Uncertain,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).Status);
                Assert.AreEqual(CadEntityReferenceStatus.Uncertain,
                    (await repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => boundary.DispatchAndCommitAsync(
                    job.Item1, job.Item2, "sketch", attempt.Id, CancellationToken.None));
                Assert.AreEqual(1, executor.CallCount);
            });
        }

        [TestMethod]
        public async Task SketchOutcomeRequiresBridgeTokenAndRollsBackModelRevisionWhenAttemptCommitFails()
        {
            await WithRepositoryAsync(async (path, repository) =>
            {
                var job = await AddJobAsync(repository, NormalizedPlan());
                var model = NewModel();
                var newPart = await repository.PrepareV2NewPartAttemptAsync(job.Item1, job.Item2, "new", model, CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(job.Item1, job.Item2, "new", model.ModelId,
                    CadCommandResult.Ok(new { modelId = model.ModelId.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }),
                    CancellationToken.None);
                var attempt = await PrepareSketchAsync(repository, job.Item1, job.Item2, "sketch", model.ModelId);
                var result = CadCommandResult.Ok(new { entityId = attempt.OutputEntityId.Value.ToString("D") });

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => repository.CommitV2SketchOutcomeAsync(
                    job.Item1, job.Item2, "sketch", model.ModelId, result, CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(newPart.ProspectiveModelRevisionId,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).CurrentModelRevisionId);

                var binding = await repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None);
                binding.NativeReferenceBytes = new byte[] { 3, 2, 1 };
                binding.Status = CadEntityReferenceStatus.Active;
                binding.UpdatedUtc = DateTime.UtcNow;
                await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
                ExecuteSql(path, "CREATE TRIGGER FailSketchApplied BEFORE UPDATE ON V2MutationAttempts " +
                    "WHEN NEW.StepKey = 'sketch' AND NEW.Status = 'Applied' BEGIN SELECT RAISE(ABORT, 'forced sketch commit failure'); END;");

                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(() => repository.CommitV2SketchOutcomeAsync(
                    job.Item1, job.Item2, "sketch", model.ModelId, result, CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Assert.AreEqual(newPart.ProspectiveModelRevisionId,
                    (await repository.GetModelAsync(model.ModelId, CancellationToken.None)).CurrentModelRevisionId,
                    "The model revision must roll back with a failed attempt transition.");

                await repository.MarkV2MutationAttemptUncertainAsync(attempt.Id, CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Uncertain,
                    (await repository.GetEntityBindingAsync(model.ModelId, attempt.OutputEntityId.Value, "Default", CancellationToken.None)).Status);
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
