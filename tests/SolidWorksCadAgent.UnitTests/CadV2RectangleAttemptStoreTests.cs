using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

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
            SqliteJobRepository repository, bool applySketch = true)
        {
            var job = await AddJobAsync(repository);
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

        private static string NormalizedPlan() => JsonConvert.SerializeObject(
            CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(Candidate).CandidateV2));

        private static async Task<Tuple<Guid, Guid>> AddJobAsync(SqliteJobRepository repository)
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
                Prompt = "rectangle reservation", PlanJson = NormalizedPlan(), CreatedUtc = now
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
