using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.SQLite;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Jobs;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SqliteJobRepositoryTests
    {
        [TestMethod]
        public async Task FullJobHistory_RoundTripsAcrossRepositoryReopen()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "SolidWorksCadAgent-" + Guid.NewGuid().ToString("N") + ".db");

            var jobId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var successCommandId = Guid.NewGuid();
            var failedCommandId = Guid.NewGuid();
            var verificationId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var created = new DateTime(2026, 9, 22, 5, 0, 0, DateTimeKind.Utc);

            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();

                    var job = new CadJob
                    {
                        Id = jobId,
                        Prompt = "Create a 100 x 60 x 10 plate with a centred 20 mm through-hole",
                        State = JobState.Interpreting,
                        PlanValidated = false,
                        HasUnresolvedAmbiguity = false,
                        OverwriteRequested = false,
                        OverwriteAuthorized = false,
                        CreatedUtc = created,
                        UpdatedUtc = created
                    };
                    await repository.CreateAsync(job);

                    await repository.AppendRevisionAsync(new JobRevision
                    {
                        Id = revisionId,
                        JobId = jobId,
                        RevisionNumber = 1,
                        Prompt = job.Prompt,
                        InterpretationJson = "{\"plate\":true}",
                        PlanJson = "{\"steps\":9}",
                        CreatedUtc = created.AddMinutes(1)
                    });

                    await repository.AppendCommandAsync(new CommandExecutionRecord
                    {
                        Id = successCommandId,
                        JobId = jobId,
                        RevisionNumber = 1,
                        SequenceNumber = 1,
                        CommandName = "NewPart",
                        ParametersJson = "{}",
                        Success = true,
                        ResultJson = "{\"documentTitle\":\"Part1\"}",
                        StartedUtc = created.AddMinutes(2),
                        CompletedUtc = created.AddMinutes(2).AddSeconds(1)
                    });

                    await repository.AppendCommandAsync(new CommandExecutionRecord
                    {
                        Id = failedCommandId,
                        JobId = jobId,
                        RevisionNumber = 1,
                        SequenceNumber = 2,
                        CommandName = "CreateSketch",
                        ParametersJson = "{\"plane\":\"Bad Plane\"}",
                        Success = false,
                        ErrorCode = "UNSUPPORTED_PARAMETER_VALUE",
                        ErrorMessage = "Plane is not allowed.",
                        StartedUtc = created.AddMinutes(3),
                        CompletedUtc = created.AddMinutes(3).AddSeconds(1)
                    });

                    await repository.AppendVerificationAsync(new VerificationResultRecord
                    {
                        Id = verificationId,
                        JobId = jobId,
                        RevisionNumber = 1,
                        CheckName = "BoundingBox",
                        Passed = true,
                        ExpectedJson = "{\"xMm\":100}",
                        ActualJson = "{\"xMm\":100}",
                        CreatedUtc = created.AddMinutes(4)
                    });

                    await repository.AddAttachmentAsync(new AttachmentRecord
                    {
                        Id = attachmentId,
                        JobId = jobId,
                        RevisionNumber = 1,
                        Kind = "SketchImage",
                        Path = @"C:\SolidWorks-CAD-Agent\Workspace\attachments\sketch.png",
                        CreatedUtc = created.AddMinutes(5)
                    });

                    job.State = JobState.AwaitingApproval;
                    job.PlanValidated = true;
                    job.OutputPath = @"C:\SolidWorks-CAD-Agent\Workspace\plate.sldprt";
                    job.UpdatedUtc = created.AddMinutes(6);
                    await repository.UpdateAsync(job);
                }

                using (var reopened = new SqliteJobRepository(databasePath))
                {
                    await reopened.InitializeAsync();
                    var snapshot = await reopened.GetSnapshotAsync(jobId);

                    Assert.IsNotNull(snapshot);
                    Assert.IsNotNull(snapshot.Job);
                    Assert.AreEqual(jobId, snapshot.Job.Id);
                    Assert.AreEqual(JobState.AwaitingApproval, snapshot.Job.State);
                    Assert.IsTrue(snapshot.Job.PlanValidated);
                    Assert.AreEqual(@"C:\SolidWorks-CAD-Agent\Workspace\plate.sldprt", snapshot.Job.OutputPath);
                    Assert.AreEqual(created.AddMinutes(6), snapshot.Job.UpdatedUtc);

                    Assert.AreEqual(1, snapshot.Revisions.Count);
                    Assert.AreEqual(revisionId, snapshot.Revisions.Single().Id);
                    Assert.AreEqual("{\"steps\":9}", snapshot.Revisions.Single().PlanJson);

                    Assert.AreEqual(2, snapshot.Commands.Count);
                    var failed = snapshot.Commands.Single(c => !c.Success);
                    Assert.AreEqual(failedCommandId, failed.Id);
                    Assert.AreEqual("UNSUPPORTED_PARAMETER_VALUE", failed.ErrorCode);

                    Assert.AreEqual(1, snapshot.Verifications.Count);
                    Assert.AreEqual(verificationId, snapshot.Verifications.Single().Id);
                    Assert.IsTrue(snapshot.Verifications.Single().Passed);

                    Assert.AreEqual(1, snapshot.Attachments.Count);
                    Assert.AreEqual(attachmentId, snapshot.Attachments.Single().Id);
                    Assert.AreEqual("SketchImage", snapshot.Attachments.Single().Kind);
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        [TestMethod]
        public async Task GetAsync_UnknownJob_ReturnsNull()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "SolidWorksCadAgent-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var job = await repository.GetAsync(Guid.NewGuid());
                    Assert.IsNull(job);
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        [TestMethod]
        public async Task TryUpdateFromStateAsync_AllowsOnlyOneCompetingTransition()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "SolidWorksCadAgent-CompareSwap-" + Guid.NewGuid().ToString("N") + ".db");
            var jobId = Guid.NewGuid();
            var created = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    await repository.CreateAsync(new CadJob
                    {
                        Id = jobId,
                        Prompt = "Create a plate",
                        State = JobState.AwaitingApproval,
                        PlanValidated = true,
                        CreatedUtc = created,
                        UpdatedUtc = created
                    });

                    var approval = await repository.GetAsync(jobId);
                    var cancellation = await repository.GetAsync(jobId);
                    approval.State = JobState.Approved;
                    cancellation.State = JobState.Cancelled;

                    Assert.IsTrue(await repository.TryUpdateFromStateAsync(approval, JobState.AwaitingApproval));
                    Assert.IsFalse(await repository.TryUpdateFromStateAsync(cancellation, JobState.AwaitingApproval));
                    Assert.AreEqual(JobState.Approved, (await repository.GetAsync(jobId)).State);
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        [TestMethod]
        public async Task InitializeAsync_MigratesVersionOneDatabaseWithoutLosingJobs()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Migration-" + Guid.NewGuid().ToString("N") + ".db");
            var jobId = Guid.NewGuid();
            try
            {
                SQLiteConnection.CreateFile(databasePath);
                using (var connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;"))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
CREATE TABLE Jobs (
    Id TEXT PRIMARY KEY NOT NULL, Prompt TEXT NOT NULL, State INTEGER NOT NULL,
    PlanValidated INTEGER NOT NULL, HasUnresolvedAmbiguity INTEGER NOT NULL,
    AmbiguityMessage TEXT NULL, OverwriteRequested INTEGER NOT NULL,
    OverwriteAuthorized INTEGER NOT NULL, OutputPath TEXT NULL,
    CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
INSERT INTO Jobs VALUES (@Id, 'Existing job', 0, 0, 0, NULL, 0, 0, NULL,
    '2026-09-22T00:00:00.0000000Z', '2026-09-22T00:00:00.0000000Z');
PRAGMA user_version = 1;";
                        command.Parameters.AddWithValue("@Id", jobId.ToString("D"));
                        command.ExecuteNonQuery();
                    }
                }

                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var existing = await repository.GetAsync(jobId);
                    Assert.IsNotNull(existing);
                    Assert.IsFalse((bool)existing.GetType().GetProperty("IsSimulated").GetValue(existing));
                }

                using (var connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;"))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA user_version;";
                        Assert.AreEqual(2L, Convert.ToInt64(command.ExecuteScalar()));
                    }
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        [TestMethod]
        public async Task IsSimulated_RoundTripsAcrossRepositoryReopen()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-SimulationMarker-" + Guid.NewGuid().ToString("N") + ".db");
            var jobId = Guid.NewGuid();
            try
            {
                using (var repository = new SqliteJobRepository(databasePath))
                {
                    await repository.InitializeAsync();
                    var job = new CadJob
                    {
                        Id = jobId,
                        Prompt = "Simulated plate",
                        State = JobState.New,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    var marker = job.GetType().GetProperty("IsSimulated");
                    Assert.IsNotNull(marker, "CadJob.IsSimulated must exist.");
                    marker.SetValue(job, true);
                    await repository.CreateAsync(job);
                }

                using (var reopened = new SqliteJobRepository(databasePath))
                {
                    await reopened.InitializeAsync();
                    var loaded = await reopened.GetAsync(jobId);
                    Assert.IsTrue((bool)loaded.GetType().GetProperty("IsSimulated").GetValue(loaded));
                }
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Test cleanup must not hide the original assertion failure.
            }
        }
    }
}
