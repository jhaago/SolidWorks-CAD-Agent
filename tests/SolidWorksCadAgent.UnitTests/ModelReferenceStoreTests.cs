using System;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ModelReferenceStoreTests
    {
        private const string HistoricalPlanJson = "{\"commands\":[{\"command\":\"CreateSketch\",\"parameters\":{\"plane\":\"Top Plane\"}}]}";

        [TestMethod]
        public async Task InitializeAsync_MigratesVersionFourWithoutChangingHistoricalPlanJson()
        {
            var path = TempDatabasePath();
            var jobId = Guid.NewGuid();
            try
            {
                CreateVersionFourFixture(path, jobId, false);
                using (var repository = new SqliteJobRepository(path))
                {
                    await repository.InitializeAsync();
                }

                Assert.AreEqual(5L, ReadUserVersion(path));
                Assert.AreEqual(HistoricalPlanJson, ReadPlanJson(path, jobId));
                Assert.AreEqual(1L, ReadScalar(path, "SELECT COUNT(*) FROM CommandExecutions WHERE JobId = @JobId;", jobId));
            }
            finally { TryDeleteDatabase(path); }
        }

        [TestMethod]
        public async Task InitializeAsync_RollsBackVersionFiveMigrationOnFailure()
        {
            var path = TempDatabasePath();
            var jobId = Guid.NewGuid();
            try
            {
                CreateVersionFourFixture(path, jobId, true);
                using (var repository = new SqliteJobRepository(path))
                {
                    await Assert.ThrowsExceptionAsync<SQLiteException>(() => repository.InitializeAsync());
                }

                Assert.AreEqual(4L, ReadUserVersion(path));
                Assert.AreEqual(HistoricalPlanJson, ReadPlanJson(path, jobId));
                Assert.AreEqual(1L, ReadScalar(path, "SELECT COUNT(*) FROM CommandExecutions WHERE JobId = @JobId;", jobId));
                Assert.IsFalse(TableExists(path, "EntityReferenceBindings"), "DDL created by the failed migration must roll back.");
            }
            finally { TryDeleteDatabase(path); }
        }

        [TestMethod]
        public async Task RegisterModelAsync_RejectsDuplicateModelId()
        {
            var path = TempDatabasePath();
            try
            {
                using (var repository = await OpenRepository(path))
                {
                    var model = NewModel(Guid.NewGuid());
                    await repository.RegisterModelAsync(model, default(System.Threading.CancellationToken));
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                        repository.RegisterModelAsync(NewModel(model.ModelId), default(System.Threading.CancellationToken)));
                }
            }
            finally { TryDeleteDatabase(path); }
        }

        [TestMethod]
        public async Task AddEntityBindingAsync_RoundTripsOpaqueBytesAndConfigurationContext()
        {
            var path = TempDatabasePath();
            try
            {
                using (var repository = await OpenRepository(path))
                {
                    var model = NewModel(Guid.NewGuid());
                    await repository.RegisterModelAsync(model, default(System.Threading.CancellationToken));
                    var originalBytes = new byte[] { 0, 17, 0, 255, 42 };
                    var binding = NewBinding(model, Guid.NewGuid(), "Default", originalBytes);
                    await repository.AddEntityBindingAsync(binding, default(System.Threading.CancellationToken));
                    originalBytes[1] = 99;

                    var loaded = await repository.GetEntityBindingAsync(model.ModelId, binding.EntityId, "Default", default(System.Threading.CancellationToken));
                    Assert.IsNotNull(loaded);
                    CollectionAssert.AreEqual(new byte[] { 0, 17, 0, 255, 42 }, loaded.NativeReferenceBytes);
                    Assert.AreEqual("Default", loaded.ConfigurationKey);
                    Assert.AreEqual("Sketch", loaded.EntityKind);
                    loaded.NativeReferenceBytes[1] = 12;
                    var reread = await repository.GetEntityBindingAsync(model.ModelId, binding.EntityId, "Default", default(System.Threading.CancellationToken));
                    CollectionAssert.AreEqual(new byte[] { 0, 17, 0, 255, 42 }, reread.NativeReferenceBytes);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, reread.Status);
                }
            }
            finally { TryDeleteDatabase(path); }
        }

        [TestMethod]
        public async Task UpdateEntityBindingAsync_PreservesLastKnownTokenWhenMarkingUnresolved()
        {
            var path = TempDatabasePath();
            try
            {
                using (var repository = await OpenRepository(path))
                {
                    var model = NewModel(Guid.NewGuid());
                    await repository.RegisterModelAsync(model, default(System.Threading.CancellationToken));
                    var binding = NewBinding(model, Guid.NewGuid(), "Default", new byte[] { 8, 6, 7, 5, 3, 0, 9 });
                    await repository.AddEntityBindingAsync(binding, default(System.Threading.CancellationToken));

                    binding.NativeReferenceBytes = null;
                    binding.Status = CadEntityReferenceStatus.Unresolved;
                    await repository.UpdateEntityBindingAsync(binding, default(System.Threading.CancellationToken));

                    var loaded = await repository.GetEntityBindingAsync(model.ModelId, binding.EntityId, "Default", default(System.Threading.CancellationToken));
                    CollectionAssert.AreEqual(new byte[] { 8, 6, 7, 5, 3, 0, 9 }, loaded.NativeReferenceBytes);
                    Assert.AreEqual(CadEntityReferenceStatus.Unresolved, loaded.Status);
                }
            }
            finally { TryDeleteDatabase(path); }
        }

        [TestMethod]
        public async Task GetEntityBindingAsync_ReturnsNoBindingForUnknownConfiguration()
        {
            var path = TempDatabasePath();
            try
            {
                using (var repository = await OpenRepository(path))
                {
                    var model = NewModel(Guid.NewGuid());
                    await repository.RegisterModelAsync(model, default(System.Threading.CancellationToken));
                    var binding = NewBinding(model, Guid.NewGuid(), "Default", new byte[] { 1, 2, 3 });
                    await repository.AddEntityBindingAsync(binding, default(System.Threading.CancellationToken));

                    var otherConfiguration = await repository.GetEntityBindingAsync(model.ModelId, binding.EntityId, "Production", default(System.Threading.CancellationToken));
                    Assert.IsNull(otherConfiguration);
                }
            }
            finally { TryDeleteDatabase(path); }
        }

        private static async Task<SqliteJobRepository> OpenRepository(string path)
        {
            var repository = new SqliteJobRepository(path);
            try
            {
                await repository.InitializeAsync();
                return repository;
            }
            catch
            {
                repository.Dispose();
                throw;
            }
        }

        private static CadModelIdentityRecord NewModel(Guid modelId)
        {
            var now = DateTime.UtcNow;
            return new CadModelIdentityRecord
            {
                ModelId = modelId,
                DocumentKind = "Part",
                Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId",
                CurrentModelRevisionId = Guid.NewGuid(),
                ConfigurationKey = "Default",
                SolidWorksRevision = "2020",
                RegistryVersion = 1,
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }

        private static CadEntityReferenceBinding NewBinding(CadModelIdentityRecord model, Guid entityId, string configuration, byte[] bytes)
        {
            return new CadEntityReferenceBinding
            {
                ModelId = model.ModelId,
                EntityId = entityId,
                EntityKind = "Sketch",
                ConfigurationKey = configuration,
                NativeObjectKind = "SketchFeature",
                ReferenceFormatVersion = 3,
                NativeReferenceBytes = bytes,
                CreatedAtModelRevisionId = model.CurrentModelRevisionId,
                LastResolvedModelRevisionId = model.CurrentModelRevisionId,
                Status = CadEntityReferenceStatus.Active,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static string TempDatabasePath() => Path.Combine(Path.GetTempPath(), "SolidWorks-ModelReference-" + Guid.NewGuid().ToString("N") + ".db");

        private static void CreateVersionFourFixture(string path, Guid jobId, bool incompatibleModelTable)
        {
            SQLiteConnection.CreateFile(path);
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
CREATE TABLE Jobs (
 Id TEXT PRIMARY KEY NOT NULL, Prompt TEXT NOT NULL, State INTEGER NOT NULL,
 PlanValidated INTEGER NOT NULL, HasUnresolvedAmbiguity INTEGER NOT NULL, AmbiguityMessage TEXT NULL,
 OverwriteRequested INTEGER NOT NULL, OverwriteAuthorized INTEGER NOT NULL, IsSimulated INTEGER NOT NULL DEFAULT 0,
 RequiresExplicitApproval INTEGER NOT NULL DEFAULT 0, OutputPath TEXT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
CREATE TABLE Revisions (
 Id TEXT PRIMARY KEY NOT NULL, JobId TEXT NOT NULL, RevisionNumber INTEGER NOT NULL, Prompt TEXT NOT NULL,
 InterpretationJson TEXT NULL, PlanJson TEXT NULL, CreatedUtc TEXT NOT NULL,
 FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE, UNIQUE (JobId, RevisionNumber));
CREATE TABLE CommandExecutions (
 Id TEXT PRIMARY KEY NOT NULL, JobId TEXT NOT NULL, RevisionNumber INTEGER NOT NULL, SequenceNumber INTEGER NOT NULL,
 CommandName TEXT NOT NULL, ParametersJson TEXT NULL, Success INTEGER NOT NULL, ResultJson TEXT NULL,
 ErrorCode TEXT NULL, ErrorMessage TEXT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NOT NULL,
 FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE, UNIQUE (JobId, RevisionNumber, SequenceNumber));
INSERT INTO Jobs (Id, Prompt, State, PlanValidated, HasUnresolvedAmbiguity, AmbiguityMessage, OverwriteRequested, OverwriteAuthorized, IsSimulated, RequiresExplicitApproval, OutputPath, CreatedUtc, UpdatedUtc)
VALUES (@JobId, 'Historical CAD job', 0, 1, 0, NULL, 0, 0, 0, 0, NULL, '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:00:00.0000000Z');
INSERT INTO Revisions (Id, JobId, RevisionNumber, Prompt, InterpretationJson, PlanJson, CreatedUtc)
VALUES (@RevisionId, @JobId, 1, 'Historical CAD job', NULL, @PlanJson, '2026-10-01T00:00:00.0000000Z');
INSERT INTO CommandExecutions (Id, JobId, RevisionNumber, SequenceNumber, CommandName, ParametersJson, Success, ResultJson, ErrorCode, ErrorMessage, StartedUtc, CompletedUtc)
VALUES (@CommandId, @JobId, 1, 1, 'CreateSketch', '{""plane"":""Top Plane""}', 1, '{""ok"":true}', NULL, NULL, '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:00:01.0000000Z');";
                    command.Parameters.AddWithValue("@JobId", jobId.ToString("D"));
                    command.Parameters.AddWithValue("@RevisionId", Guid.NewGuid().ToString("D"));
                    command.Parameters.AddWithValue("@CommandId", Guid.NewGuid().ToString("D"));
                    command.Parameters.AddWithValue("@PlanJson", HistoricalPlanJson);
                    command.ExecuteNonQuery();
                }
                if (incompatibleModelTable)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TABLE ManagedModels (ModelId TEXT PRIMARY KEY NOT NULL, Unexpected TEXT NULL); PRAGMA user_version = 4;";
                        command.ExecuteNonQuery();
                    }
                }
                else
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA user_version = 4;";
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private static long ReadUserVersion(string path) => ReadScalar(path, "PRAGMA user_version;");

        private static long ReadScalar(string path, string sql, Guid? jobId = null)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    if (jobId.HasValue) command.Parameters.AddWithValue("@JobId", jobId.Value.ToString("D"));
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static string ReadPlanJson(string path, Guid jobId)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT PlanJson FROM Revisions WHERE JobId = @JobId AND RevisionNumber = 1;";
                command.Parameters.AddWithValue("@JobId", jobId.ToString("D"));
                return Convert.ToString(command.ExecuteScalar());
            }
        }

        private static bool TableExists(string path, string table)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @Name;";
                command.Parameters.AddWithValue("@Name", table);
                return Convert.ToInt64(command.ExecuteScalar()) != 0;
            }
        }

        private static void TryDeleteDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}
