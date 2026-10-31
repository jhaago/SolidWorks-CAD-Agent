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
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class V2SequentialPersistenceTests
    {
        [TestMethod]
        public async Task MultipleSketchesCommitAgainstImmediatePredecessorAndOwnProducer()
        {
            await WithFixture(async f =>
            {
                foreach (var key in new[] { "sketch", "circle", "exit", "boss", "sketch2", "circle2", "exit2", "cut" })
                    await Apply(f, key);
                var save = await f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "save", f.Model, CancellationToken.None);
                var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sequential.sldprt"));
                await f.Repository.CommitV2SequentialSaveOutcomeAsync(f.Job, f.Revision, "save", f.Model, path,
                    new string('a', 64), CadCommandResult.Ok(new { path, errors = 0 }), CancellationToken.None);
                var model = await f.Repository.GetModelAsync(f.Model, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.ActiveSaved, model.Status);
                Assert.AreEqual(save.ProspectiveModelRevisionId, model.CurrentModelRevisionId);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.ValidateV2SequentialAttemptAsync(f.Job, f.Revision, "save", save.Id, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task UnappliedPredecessorWrongAttemptAndUncertaintyNeverAuthorizeReplay()
        {
            await WithFixture(async f =>
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "circle", f.Model, CancellationToken.None));
                await Apply(f, "sketch");
                var attempt = await f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "circle", f.Model, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.ValidateV2SequentialAttemptAsync(f.Job, f.Revision, "circle", Guid.NewGuid(), CancellationToken.None));
                await f.Repository.MarkV2MutationAttemptUncertainAsync(attempt.Id, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.ValidateV2SequentialAttemptAsync(f.Job, f.Revision, "circle", attempt.Id, CancellationToken.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "circle", f.Model, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task RawPlanChangeAndWrongObservedGeometryLeaveAttemptPrepared()
        {
            await WithFixture(async f =>
            {
                await Apply(f, "sketch");
                var attempt = await f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "circle", f.Model, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.CommitV2SequentialOutcomeAsync(f.Job, f.Revision, "circle", f.Model,
                        CadCommandResult.Ok(new { centerXmm = 0, centerYmm = 0, diameterMm = 999 }), CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await f.Repository.GetV2MutationAttemptAsync(attempt.Id, CancellationToken.None)).Status);
                Sql(f.Path, "UPDATE Revisions SET PlanJson = PlanJson || ' ';");
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.ValidateV2SequentialAttemptAsync(f.Job, f.Revision, "circle", attempt.Id, CancellationToken.None));
            });
        }

        [TestMethod]
        public async Task CommitFailureRollsBackRevisionAndSecondSketchNeedsItsOwnBinding()
        {
            await WithFixture(async f =>
            {
                foreach (var key in new[] { "sketch", "circle", "exit", "boss" }) await Apply(f, key);
                var previous = (await f.Repository.GetModelAsync(f.Model, CancellationToken.None)).CurrentModelRevisionId;
                var sketch = await f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, "sketch2", f.Model, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    f.Repository.CommitV2SequentialOutcomeAsync(f.Job, f.Revision, "sketch2", f.Model,
                        CadCommandResult.Ok(new { entityId = sketch.OutputEntityId.Value.ToString("D") }), CancellationToken.None));
                await ActivateBinding(f, sketch);
                Sql(f.Path, "CREATE TRIGGER FailApplied BEFORE UPDATE OF Status ON V2MutationAttempts WHEN NEW.StepKey = 'sketch2' AND NEW.Status = 'Applied' BEGIN SELECT RAISE(ABORT, 'forced'); END;");
                try
                {
                    await f.Repository.CommitV2SequentialOutcomeAsync(f.Job, f.Revision, "sketch2", f.Model,
                        CadCommandResult.Ok(new { entityId = sketch.OutputEntityId.Value.ToString("D") }), CancellationToken.None);
                    Assert.Fail("Expected transactional failure.");
                }
                catch (System.Data.SQLite.SQLiteException) { }
                Assert.AreEqual(previous, (await f.Repository.GetModelAsync(f.Model, CancellationToken.None)).CurrentModelRevisionId);
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,
                    (await f.Repository.GetV2MutationAttemptAsync(sketch.Id, CancellationToken.None)).Status);
            });
        }

        private static async Task Apply(Fixture f, string key)
        {
            var step = f.Plan.Steps.Find(s => s.StepKey == key);
            var attempt = await f.Repository.PrepareV2SequentialAttemptAsync(f.Job, f.Revision, key, f.Model, CancellationToken.None);
            Assert.AreEqual(attempt.Id, (await f.Repository.ValidateV2SequentialAttemptAsync(f.Job, f.Revision, key, attempt.Id, CancellationToken.None)).Id);
            CadCommandResult result;
            if (step.Command == "CreateSketch")
            {
                await ActivateBinding(f, attempt);
                result = CadCommandResult.Ok(new { entityId = attempt.OutputEntityId.Value.ToString("D") });
            }
            else if (step.Command == "ExitSketch") result = CadCommandResult.Ok(new { exited = true });
            else if (step.Command == "Extrude" || step.Command == "CutExtrude")
                result = CadCommandResult.Ok(new { depthMm = step.Parameters.Value<double>("depthMm"), endCondition = step.Parameters.Value<string>("endCondition"), featureName = key });
            else result = CadCommandResult.Ok(step.Parameters);
            await f.Repository.CommitV2SequentialOutcomeAsync(f.Job, f.Revision, key, f.Model, result, CancellationToken.None);
            Assert.AreEqual(attempt.ProspectiveModelRevisionId,
                (await f.Repository.GetModelAsync(f.Model, CancellationToken.None)).CurrentModelRevisionId);
        }

        private static async Task ActivateBinding(Fixture f, CadV2MutationAttemptRecord attempt)
        {
            var binding = await f.Repository.GetEntityBindingAsync(f.Model, attempt.OutputEntityId.Value, "Default", CancellationToken.None);
            binding.NativeReferenceBytes = new byte[] { 1, 2, 3 };
            binding.Status = CadEntityReferenceStatus.Active;
            binding.UpdatedUtc = DateTime.UtcNow;
            await f.Repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
        }

        private static async Task WithFixture(Func<Fixture, Task> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "v2-sequential-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                using (var repository = new SqliteJobRepository(path))
                {
                    await repository.InitializeAsync();
                    var candidate = JObject.Parse(@"{'planVersion':2,'steps':[
{'stepKey':'new','command':'NewPart','operationVersion':1,'parameters':{}},
{'stepKey':'sketch','command':'CreateSketch','operationVersion':1,'parameters':{'plane':'Top Plane'},'outputKey':'profile'},
{'stepKey':'circle','command':'AddRectangle','operationVersion':1,'parameters':{'centerXmm':0,'centerYmm':0,'widthMm':20,'heightMm':10}},
{'stepKey':'exit','command':'ExitSketch','operationVersion':1,'parameters':{}},
{'stepKey':'boss','command':'Extrude','operationVersion':2,'parameters':{'depthMm':5},'inputs':{'profileSketch':{'kind':'Sketch','outputKey':'profile'}}},
{'stepKey':'sketch2','command':'CreateSketch','operationVersion':1,'parameters':{'plane':'Top Plane'},'outputKey':'hole'},
{'stepKey':'circle2','command':'AddCircle','operationVersion':1,'parameters':{'centerXmm':0,'centerYmm':0,'diameterMm':2}},
{'stepKey':'exit2','command':'ExitSketch','operationVersion':1,'parameters':{}},
{'stepKey':'cut','command':'CutExtrude','operationVersion':2,'parameters':{'endCondition':'Blind','depthMm':2},'inputs':{'profileSketch':{'kind':'Sketch','outputKey':'hole'}}},
{'stepKey':'save','command':'SavePart','operationVersion':1,'parameters':{'path':'sequential.sldprt','allowOverwrite':false}}]}");
                    var parsed = CadPlanDocumentReader.ReadCandidate(candidate.ToString());
                    Assert.IsTrue(parsed.IsValid, string.Join(" ", parsed.Errors));
                    var plan = CadPlanV2HostNormalizer.Normalize(parsed.CandidateV2);
                    Assert.AreEqual(5.0, CadV2PrismaticGeometry.Analyze(plan).DepthMm);
                    var f = new Fixture { Repository = repository, Job = Guid.NewGuid(), Revision = Guid.NewGuid(), Model = Guid.NewGuid(), Plan = plan, Path = path };
                    var now = DateTime.UtcNow;
                    await repository.CreateAsync(new CadJob { Id = f.Job, Prompt = "sequential", State = JobState.Executing, PlanValidated = true, CreatedUtc = now, UpdatedUtc = now });
                    await repository.AppendRevisionAsync(new JobRevision { Id = f.Revision, JobId = f.Job, RevisionNumber = 1, Prompt = "sequential", PlanJson = JsonConvert.SerializeObject(plan), CreatedUtc = now });
                    var model = new CadModelIdentityRecord { ModelId = f.Model, DocumentKind = "Part", Status = CadModelIdentityStatus.Pending, CustomPropertyKey = "SolidWorksCadAgent.ModelId", CurrentModelRevisionId = Guid.NewGuid(), ConfigurationKey = "Default", RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now };
                    await repository.PrepareV2NewPartAttemptAsync(f.Job, f.Revision, "new", model, CancellationToken.None);
                    await repository.CommitV2NewPartOutcomeAsync(f.Job, f.Revision, "new", f.Model, CadCommandResult.Ok(new { modelId = f.Model.ToString("D"), documentTitle = "Part1", configurationKey = "Default" }), CancellationToken.None);
                    await action(f);
                }
            }
            finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        }

        private static void Sql(string path, string sql)
        {
            using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (var command = connection.CreateCommand()) { connection.Open(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }

        private sealed class Fixture
        {
            internal SqliteJobRepository Repository;
            internal Guid Job, Revision, Model;
            internal CadPlanV2Document Plan;
            internal string Path;
        }
    }
}
