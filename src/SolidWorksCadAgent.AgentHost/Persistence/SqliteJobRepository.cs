using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Jobs;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed class SqliteJobRepository : IDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private bool _disposed;

        public SqliteJobRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A database path is required.", nameof(databasePath));

            _databasePath = Path.GetFullPath(databasePath);
            _connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Version = 3,
                ForeignKeys = true
            }.ConnectionString;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var connection = OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = LoadSchema();
                    command.ExecuteNonQuery();
                }
                EnsureSchemaVersion(connection);
            }

            return Task.CompletedTask;
        }

        public Task CreateAsync(CadJob job, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO Jobs
(Id, Prompt, State, PlanValidated, HasUnresolvedAmbiguity, AmbiguityMessage,
 OverwriteRequested, OverwriteAuthorized, IsSimulated, OutputPath, CreatedUtc, UpdatedUtc)
VALUES
(@Id, @Prompt, @State, @PlanValidated, @HasUnresolvedAmbiguity, @AmbiguityMessage,
 @OverwriteRequested, @OverwriteAuthorized, @IsSimulated, @OutputPath, @CreatedUtc, @UpdatedUtc);";
                BindJob(command, job);
                command.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }

        public Task<CadJob> GetAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var connection = OpenConnection())
            {
                return Task.FromResult(ReadJob(connection, null, id));
            }
        }

        public Task<JobPageDto> ListAsync(
            int limit,
            string cursor,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (limit < 1 || limit > 100)
                throw new ArgumentOutOfRangeException(nameof(limit), "The page size must be between 1 and 100.");

            DateTime cursorUpdatedUtc;
            Guid cursorId;
            var hasCursor = !string.IsNullOrWhiteSpace(cursor);
            if (hasCursor)
                DecodeCursor(cursor, out cursorUpdatedUtc, out cursorId);
            else
            {
                cursorUpdatedUtc = default(DateTime);
                cursorId = Guid.Empty;
            }

            var items = new List<JobSummaryDto>();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT Id, Prompt, State, IsSimulated, CreatedUtc, UpdatedUtc
FROM Jobs
WHERE @HasCursor = 0
   OR UpdatedUtc < @CursorUpdatedUtc
   OR (UpdatedUtc = @CursorUpdatedUtc AND Id < @CursorId)
ORDER BY UpdatedUtc DESC, Id DESC
LIMIT @Take;";
                Add(command, "@HasCursor", hasCursor ? 1 : 0);
                Add(command, "@CursorUpdatedUtc", hasCursor ? DateText(cursorUpdatedUtc) : string.Empty);
                Add(command, "@CursorId", hasCursor ? GuidText(cursorId) : string.Empty);
                Add(command, "@Take", limit + 1);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(new JobSummaryDto
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            Prompt = reader.GetString(1),
                            State = (JobState)reader.GetInt32(2),
                            IsSimulated = reader.GetInt32(3) != 0,
                            CreatedUtc = ParseDate(reader.GetString(4)),
                            UpdatedUtc = ParseDate(reader.GetString(5))
                        });
                    }
                }
            }

            string nextCursor = null;
            if (items.Count > limit)
            {
                items.RemoveAt(items.Count - 1);
                var last = items[items.Count - 1];
                nextCursor = EncodeCursor(last.UpdatedUtc, last.Id);
            }

            return Task.FromResult(new JobPageDto { Items = items, NextCursor = nextCursor });
        }

        public Task UpdateAsync(CadJob job, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE Jobs SET
    Prompt = @Prompt,
    State = @State,
    PlanValidated = @PlanValidated,
    HasUnresolvedAmbiguity = @HasUnresolvedAmbiguity,
    AmbiguityMessage = @AmbiguityMessage,
    OverwriteRequested = @OverwriteRequested,
    OverwriteAuthorized = @OverwriteAuthorized,
    IsSimulated = @IsSimulated,
    OutputPath = @OutputPath,
    CreatedUtc = @CreatedUtc,
    UpdatedUtc = @UpdatedUtc
WHERE Id = @Id;";
                BindJob(command, job);
                EnsureOneRow(command.ExecuteNonQuery(), "update", job.Id);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateFromStateAsync(
            CadJob job,
            JobState expectedState,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE Jobs SET
    Prompt = @Prompt,
    State = @State,
    PlanValidated = @PlanValidated,
    HasUnresolvedAmbiguity = @HasUnresolvedAmbiguity,
    AmbiguityMessage = @AmbiguityMessage,
    OverwriteRequested = @OverwriteRequested,
    OverwriteAuthorized = @OverwriteAuthorized,
    IsSimulated = @IsSimulated,
    OutputPath = @OutputPath,
    CreatedUtc = @CreatedUtc,
    UpdatedUtc = @UpdatedUtc
WHERE Id = @Id AND State = @ExpectedState;";
                BindJob(command, job);
                Add(command, "@ExpectedState", (int)expectedState);
                return Task.FromResult(command.ExecuteNonQuery() == 1);
            }
        }

        public Task AppendRevisionAsync(JobRevision revision, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (revision == null) throw new ArgumentNullException(nameof(revision));

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO Revisions
(Id, JobId, RevisionNumber, Prompt, InterpretationJson, PlanJson, CreatedUtc)
VALUES
(@Id, @JobId, @RevisionNumber, @Prompt, @InterpretationJson, @PlanJson, @CreatedUtc);";
                Add(command, "@Id", GuidText(revision.Id));
                Add(command, "@JobId", GuidText(revision.JobId));
                Add(command, "@RevisionNumber", revision.RevisionNumber);
                Add(command, "@Prompt", revision.Prompt);
                Add(command, "@InterpretationJson", revision.InterpretationJson);
                Add(command, "@PlanJson", revision.PlanJson);
                Add(command, "@CreatedUtc", DateText(revision.CreatedUtc));
                command.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }

        public Task AppendCommandAsync(CommandExecutionRecord record, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (record == null) throw new ArgumentNullException(nameof(record));

            using (var connection = OpenConnection())
            {
                InsertCommand(connection, null, record);
            }

            return Task.CompletedTask;
        }

        public Task AppendVerificationAsync(VerificationResultRecord record, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (record == null) throw new ArgumentNullException(nameof(record));

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO VerificationResults
(Id, JobId, RevisionNumber, CheckName, Passed, ExpectedJson, ActualJson, CreatedUtc)
VALUES
(@Id, @JobId, @RevisionNumber, @CheckName, @Passed, @ExpectedJson, @ActualJson, @CreatedUtc);";
                Add(command, "@Id", GuidText(record.Id));
                Add(command, "@JobId", GuidText(record.JobId));
                Add(command, "@RevisionNumber", record.RevisionNumber);
                Add(command, "@CheckName", record.CheckName);
                Add(command, "@Passed", record.Passed ? 1 : 0);
                Add(command, "@ExpectedJson", record.ExpectedJson);
                Add(command, "@ActualJson", record.ActualJson);
                Add(command, "@CreatedUtc", DateText(record.CreatedUtc));
                command.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }

        public Task AddAttachmentAsync(AttachmentRecord record, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (record == null) throw new ArgumentNullException(nameof(record));

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO Attachments
(Id, JobId, RevisionNumber, Kind, Path, CreatedUtc)
VALUES
(@Id, @JobId, @RevisionNumber, @Kind, @Path, @CreatedUtc);";
                Add(command, "@Id", GuidText(record.Id));
                Add(command, "@JobId", GuidText(record.JobId));
                Add(command, "@RevisionNumber", record.RevisionNumber);
                Add(command, "@Kind", record.Kind);
                Add(command, "@Path", record.Path);
                Add(command, "@CreatedUtc", DateText(record.CreatedUtc));
                command.ExecuteNonQuery();
            }

            return Task.CompletedTask;
        }

        public Task UpdateAndAppendCommandAsync(
            CadJob job,
            CommandExecutionRecord record,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.JobId != job.Id)
                throw new ArgumentException("The command record must belong to the job being updated.", nameof(record));

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
UPDATE Jobs SET
    Prompt = @Prompt,
    State = @State,
    PlanValidated = @PlanValidated,
    HasUnresolvedAmbiguity = @HasUnresolvedAmbiguity,
    AmbiguityMessage = @AmbiguityMessage,
    OverwriteRequested = @OverwriteRequested,
    OverwriteAuthorized = @OverwriteAuthorized,
    IsSimulated = @IsSimulated,
    OutputPath = @OutputPath,
    CreatedUtc = @CreatedUtc,
    UpdatedUtc = @UpdatedUtc
WHERE Id = @Id;";
                    BindJob(command, job);
                    EnsureOneRow(command.ExecuteNonQuery(), "update", job.Id);
                }

                InsertCommand(connection, transaction, record);
                transaction.Commit();
            }

            return Task.CompletedTask;
        }

        public Task<JobSnapshot> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var job = ReadJob(connection, transaction, id);
                if (job == null)
                {
                    transaction.Commit();
                    return Task.FromResult<JobSnapshot>(null);
                }

                var snapshot = new JobSnapshot
                {
                    Job = job,
                    Revisions = ReadRevisions(connection, transaction, id),
                    Commands = ReadCommands(connection, transaction, id),
                    Verifications = ReadVerifications(connection, transaction, id),
                    Attachments = ReadAttachments(connection, transaction, id)
                };
                transaction.Commit();
                return Task.FromResult(snapshot);
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        private static CadJob ReadJob(SQLiteConnection connection, SQLiteTransaction transaction, Guid id)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, Prompt, State, PlanValidated, HasUnresolvedAmbiguity, AmbiguityMessage,
       OverwriteRequested, OverwriteAuthorized, IsSimulated, OutputPath, CreatedUtc, UpdatedUtc
FROM Jobs WHERE Id = @Id;";
                Add(command, "@Id", GuidText(id));

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new CadJob
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        Prompt = reader.GetString(1),
                        State = (JobState)reader.GetInt32(2),
                        PlanValidated = reader.GetInt32(3) != 0,
                        HasUnresolvedAmbiguity = reader.GetInt32(4) != 0,
                        AmbiguityMessage = NullableString(reader, 5),
                        OverwriteRequested = reader.GetInt32(6) != 0,
                        OverwriteAuthorized = reader.GetInt32(7) != 0,
                        IsSimulated = reader.GetInt32(8) != 0,
                        OutputPath = NullableString(reader, 9),
                        CreatedUtc = ParseDate(reader.GetString(10)),
                        UpdatedUtc = ParseDate(reader.GetString(11))
                    };
                }
            }
        }

        private static List<JobRevision> ReadRevisions(SQLiteConnection connection, SQLiteTransaction transaction, Guid jobId)
        {
            var result = new List<JobRevision>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, JobId, RevisionNumber, Prompt, InterpretationJson, PlanJson, CreatedUtc
FROM Revisions WHERE JobId = @JobId ORDER BY RevisionNumber;";
                Add(command, "@JobId", GuidText(jobId));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new JobRevision
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            JobId = Guid.Parse(reader.GetString(1)),
                            RevisionNumber = reader.GetInt32(2),
                            Prompt = reader.GetString(3),
                            InterpretationJson = NullableString(reader, 4),
                            PlanJson = NullableString(reader, 5),
                            CreatedUtc = ParseDate(reader.GetString(6))
                        });
                    }
                }
            }
            return result;
        }

        private static List<CommandExecutionRecord> ReadCommands(SQLiteConnection connection, SQLiteTransaction transaction, Guid jobId)
        {
            var result = new List<CommandExecutionRecord>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, JobId, RevisionNumber, SequenceNumber, CommandName, ParametersJson, Success,
       ResultJson, ErrorCode, ErrorMessage, StartedUtc, CompletedUtc
FROM CommandExecutions WHERE JobId = @JobId ORDER BY RevisionNumber, SequenceNumber;";
                Add(command, "@JobId", GuidText(jobId));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new CommandExecutionRecord
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            JobId = Guid.Parse(reader.GetString(1)),
                            RevisionNumber = reader.GetInt32(2),
                            SequenceNumber = reader.GetInt32(3),
                            CommandName = reader.GetString(4),
                            ParametersJson = NullableString(reader, 5),
                            Success = reader.GetInt32(6) != 0,
                            ResultJson = NullableString(reader, 7),
                            ErrorCode = NullableString(reader, 8),
                            ErrorMessage = NullableString(reader, 9),
                            StartedUtc = ParseDate(reader.GetString(10)),
                            CompletedUtc = ParseDate(reader.GetString(11))
                        });
                    }
                }
            }
            return result;
        }

        private static List<VerificationResultRecord> ReadVerifications(SQLiteConnection connection, SQLiteTransaction transaction, Guid jobId)
        {
            var result = new List<VerificationResultRecord>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, JobId, RevisionNumber, CheckName, Passed, ExpectedJson, ActualJson, CreatedUtc
FROM VerificationResults WHERE JobId = @JobId ORDER BY RevisionNumber, CreatedUtc;";
                Add(command, "@JobId", GuidText(jobId));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new VerificationResultRecord
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            JobId = Guid.Parse(reader.GetString(1)),
                            RevisionNumber = reader.GetInt32(2),
                            CheckName = reader.GetString(3),
                            Passed = reader.GetInt32(4) != 0,
                            ExpectedJson = NullableString(reader, 5),
                            ActualJson = NullableString(reader, 6),
                            CreatedUtc = ParseDate(reader.GetString(7))
                        });
                    }
                }
            }
            return result;
        }

        private static List<AttachmentRecord> ReadAttachments(SQLiteConnection connection, SQLiteTransaction transaction, Guid jobId)
        {
            var result = new List<AttachmentRecord>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, JobId, RevisionNumber, Kind, Path, CreatedUtc
FROM Attachments WHERE JobId = @JobId ORDER BY RevisionNumber, CreatedUtc;";
                Add(command, "@JobId", GuidText(jobId));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new AttachmentRecord
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            JobId = Guid.Parse(reader.GetString(1)),
                            RevisionNumber = reader.GetInt32(2),
                            Kind = reader.GetString(3),
                            Path = reader.GetString(4),
                            CreatedUtc = ParseDate(reader.GetString(5))
                        });
                    }
                }
            }
            return result;
        }

        private static void InsertCommand(SQLiteConnection connection, SQLiteTransaction transaction, CommandExecutionRecord record)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO CommandExecutions
(Id, JobId, RevisionNumber, SequenceNumber, CommandName, ParametersJson, Success,
 ResultJson, ErrorCode, ErrorMessage, StartedUtc, CompletedUtc)
VALUES
(@Id, @JobId, @RevisionNumber, @SequenceNumber, @CommandName, @ParametersJson, @Success,
 @ResultJson, @ErrorCode, @ErrorMessage, @StartedUtc, @CompletedUtc);";
                Add(command, "@Id", GuidText(record.Id));
                Add(command, "@JobId", GuidText(record.JobId));
                Add(command, "@RevisionNumber", record.RevisionNumber);
                Add(command, "@SequenceNumber", record.SequenceNumber);
                Add(command, "@CommandName", record.CommandName);
                Add(command, "@ParametersJson", record.ParametersJson);
                Add(command, "@Success", record.Success ? 1 : 0);
                Add(command, "@ResultJson", record.ResultJson);
                Add(command, "@ErrorCode", record.ErrorCode);
                Add(command, "@ErrorMessage", record.ErrorMessage);
                Add(command, "@StartedUtc", DateText(record.StartedUtc));
                Add(command, "@CompletedUtc", DateText(record.CompletedUtc));
                command.ExecuteNonQuery();
            }
        }

        private static void BindJob(SQLiteCommand command, CadJob job)
        {
            Add(command, "@Id", GuidText(job.Id));
            Add(command, "@Prompt", job.Prompt);
            Add(command, "@State", (int)job.State);
            Add(command, "@PlanValidated", job.PlanValidated ? 1 : 0);
            Add(command, "@HasUnresolvedAmbiguity", job.HasUnresolvedAmbiguity ? 1 : 0);
            Add(command, "@AmbiguityMessage", job.AmbiguityMessage);
            Add(command, "@OverwriteRequested", job.OverwriteRequested ? 1 : 0);
            Add(command, "@OverwriteAuthorized", job.OverwriteAuthorized ? 1 : 0);
            Add(command, "@IsSimulated", job.IsSimulated ? 1 : 0);
            Add(command, "@OutputPath", job.OutputPath);
            Add(command, "@CreatedUtc", DateText(job.CreatedUtc));
            Add(command, "@UpdatedUtc", DateText(job.UpdatedUtc));
        }

        private static void Add(SQLiteCommand command, string name, object value)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private static string NullableString(SQLiteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static string GuidText(Guid value)
        {
            if (value == Guid.Empty) throw new ArgumentException("Persistent record IDs must not be empty GUIDs.");
            return value.ToString("D");
        }

        private static string DateText(DateTime value)
        {
            if (value == default(DateTime)) throw new ArgumentException("Persistent timestamps must be populated.");
            var utc = value.Kind == DateTimeKind.Utc
                ? value
                : value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : value.ToUniversalTime();
            return utc.ToString("O", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseDate(string value)
        {
            var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }

        private static string EncodeCursor(DateTime updatedUtc, Guid id)
        {
            var bytes = Encoding.UTF8.GetBytes(DateText(updatedUtc) + "|" + GuidText(id));
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void DecodeCursor(string cursor, out DateTime updatedUtc, out Guid id)
        {
            try
            {
                var encoded = cursor.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
                var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var separator = value.IndexOf('|');
                if (separator <= 0 || separator == value.Length - 1)
                    throw new FormatException("The cursor payload is incomplete.");

                updatedUtc = ParseDate(value.Substring(0, separator));
                id = Guid.Parse(value.Substring(separator + 1));
                if (id == Guid.Empty) throw new FormatException("The cursor ID is empty.");
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                throw new ArgumentException("The job cursor is invalid.", nameof(cursor), ex);
            }
        }

        private static void ValidateJob(CadJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (job.Id == Guid.Empty) throw new ArgumentException("Job ID is required.", nameof(job));
            if (string.IsNullOrWhiteSpace(job.Prompt)) throw new ArgumentException("Job prompt is required.", nameof(job));
            DateText(job.CreatedUtc);
            DateText(job.UpdatedUtc);
        }

        private static void EnsureOneRow(int affectedRows, string operation, Guid jobId)
        {
            if (affectedRows != 1)
                throw new KeyNotFoundException(string.Format("Unable to {0} CAD job {1}; it does not exist.", operation, jobId));
        }

        private static string LoadSchema()
        {
            var assembly = typeof(SqliteJobRepository).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith(".Persistence.Schema.sql", StringComparison.Ordinal));
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("SQLite schema resource is missing.")))
            {
                return reader.ReadToEnd();
            }
        }

        private static void EnsureSchemaVersion(SQLiteConnection connection)
        {
            if (!HasColumn(connection, "Jobs", "IsSimulated"))
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "ALTER TABLE Jobs ADD COLUMN IsSimulated INTEGER NOT NULL DEFAULT 0;";
                    command.ExecuteNonQuery();
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version = 2;";
                command.ExecuteNonQuery();
            }
        }

        private static bool HasColumn(SQLiteConnection connection, string table, string column)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteJobRepository));
        }
    }
}
