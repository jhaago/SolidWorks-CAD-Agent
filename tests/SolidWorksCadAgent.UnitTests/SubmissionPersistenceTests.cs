using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.SQLite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SubmissionPersistenceTests
    {
        private string path;
        [TestInitialize] public void Setup() => path = Path.Combine(Path.GetTempPath(), "Submission-" + Guid.NewGuid().ToString("N") + ".db");
        [TestCleanup] public void Cleanup() { SQLiteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
        private static CadJob Job(Guid id) => new CadJob { Id = id, Prompt = "plate", State = JobState.New, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        private async Task<SqliteJobRepository> Open() { var r = new SqliteJobRepository(path); await r.InitializeAsync(); return r; }

        [TestMethod]
        public async Task ConcurrentCreate_ReturnsOneCreator()
        {
            using (var first = await Open()) using (var second = await Open())
            {
                var id = Guid.NewGuid(); var hash = SubmissionInputHash.Compute("plate", null);
                using (var start = new ManualResetEventSlim())
                {
                    var tasks = new[] { first, second }.Select(r => Task.Run(async () => { start.Wait(); return await r.CreateSubmissionAsync(Job(id), null, hash, CancellationToken.None); })).ToArray();
                    start.Set(); var results = await Task.WhenAll(tasks);
                    Assert.AreEqual(1, results.Count(r => r.Created));
                    Assert.IsTrue(results.All(r => r.Job.Id == id));
                }
            }
        }

        [TestMethod]
        public async Task RepeatAfterRepositoryReopen_ReturnsExisting()
        {
            var id = Guid.NewGuid(); var image = new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1, 2, 3 } };
            var hash = SubmissionInputHash.Compute("plate", image);
            using (var r = await Open()) Assert.IsTrue((await r.CreateSubmissionAsync(Job(id), image, hash, CancellationToken.None)).Created);
            using (var r = await Open())
            {
                Assert.IsFalse((await r.CreateSubmissionAsync(Job(id), image, hash, CancellationToken.None)).Created);
                CollectionAssert.AreEqual(image.Bytes, (await r.GetInputImageAsync(id)).Bytes);
                Assert.AreEqual(id, (await r.FindSubmissionAsync(id, hash, CancellationToken.None)).Id);
                var conflict = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() => r.CreateSubmissionAsync(Job(id), image, "different", CancellationToken.None));
                Assert.AreEqual("SUBMISSION_CONFLICT", conflict.Code);
            }
        }

        [TestMethod]
        public async Task LegacyIdCannotBeClaimed()
        {
            using (var r = await Open())
            {
                var id = Guid.NewGuid(); await r.CreateAsync(Job(id));
                var e = await Assert.ThrowsExceptionAsync<JobCoordinatorException>(() => r.FindSubmissionAsync(id, SubmissionInputHash.Compute("plate", null), CancellationToken.None));
                Assert.AreEqual("SUBMISSION_CONFLICT", e.Code);
            }
        }

        [TestMethod]
        public async Task RevisionPromptDoesNotChangeSubmissionIdentity()
        {
            using (var r = await Open())
            {
                var id = Guid.NewGuid(); var hash = SubmissionInputHash.Compute("plate", null);
                var job = (await r.CreateSubmissionAsync(Job(id), null, hash, CancellationToken.None)).Job;
                job.Prompt = "revised plate"; await r.UpdateAsync(job);
                Assert.AreEqual("revised plate", (await r.FindSubmissionAsync(id, hash, CancellationToken.None)).Prompt);
            }
        }

        [TestMethod]
        public async Task ImageAndJobRollbackTogether()
        {
            using (var r = await Open())
            {
                using (var c = new SQLiteConnection("Data Source=" + path))
                {
                    c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE TRIGGER RejectImage BEFORE INSERT ON JobInputImages BEGIN SELECT RAISE(ABORT, 'fixture'); END;"; cmd.ExecuteNonQuery(); }
                }
                var id = Guid.NewGuid(); var image = new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1 } };
                await Assert.ThrowsExceptionAsync<SQLiteException>(() => r.CreateSubmissionAsync(Job(id), image, SubmissionInputHash.Compute("plate", image), CancellationToken.None));
                Assert.IsNull(await r.GetAsync(id)); Assert.IsNull(await r.GetInputImageAsync(id));
            }
        }
    }
}
