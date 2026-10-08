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
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository : IDisposable, IModelReferenceStore
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
                    command.CommandText = "PRAGMA journal_mode = WAL;";
                    command.ExecuteScalar();
                }
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    try
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = LoadSchema();
                            command.ExecuteNonQuery();
                        }
                        EnsureSchemaVersion(connection, transaction);
                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }

            return Task.CompletedTask;
        }

        // Called once at host startup. Never replay CAD work after an interrupted process.
        public Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var now = DateText(DateTime.UtcNow);
                using (var models = connection.CreateCommand())
                {
                    models.Transaction = transaction;
                    models.CommandText = @"
UPDATE ManagedModels SET Status = @Uncertain, UpdatedUtc = @Now
WHERE ModelId IN (SELECT ModelId FROM V2MutationAttempts WHERE Status = 'Prepared');";
                    Add(models, "@Uncertain", CadModelIdentityStatus.Uncertain.ToString());
                    Add(models, "@Now", now);
                    models.ExecuteNonQuery();
                }
                using (var attempts = connection.CreateCommand())
                {
                    attempts.Transaction = transaction;
                    attempts.CommandText = "UPDATE V2MutationAttempts SET Status = 'Uncertain', UpdatedUtc = @Now WHERE Status = 'Prepared';";
                    Add(attempts, "@Now", now);
                    attempts.ExecuteNonQuery();
                }
                using (var jobs = connection.CreateCommand())
                {
                    jobs.Transaction = transaction;
                    jobs.CommandText = "UPDATE Jobs SET State = @Failed, UpdatedUtc = @Now WHERE State IN (@New, @Interpreting, @Approved, @Executing, @Verifying);";
                    Add(jobs, "@Failed", (int)JobState.Failed);
                    Add(jobs, "@Now", now);
                    Add(jobs, "@New", (int)JobState.New);
                    Add(jobs, "@Interpreting", (int)JobState.Interpreting);
                    Add(jobs, "@Approved", (int)JobState.Approved);
                    Add(jobs, "@Executing", (int)JobState.Executing);
                    Add(jobs, "@Verifying", (int)JobState.Verifying);
                    var recovered = jobs.ExecuteNonQuery();
                    transaction.Commit();
                    return Task.FromResult(recovered);
                }
            }
        }

        public Task CreateAsync(CadJob job, CancellationToken cancellationToken = default(CancellationToken))
            => CreateAsync(job, null, cancellationToken);

        public Task CreateAsync(CadJob job, CadPlanningImage image, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO Jobs
(Id, Prompt, State, PlanValidated, HasUnresolvedAmbiguity, AmbiguityMessage,
 OverwriteRequested, OverwriteAuthorized, IsSimulated, OutputPath, CreatedUtc, UpdatedUtc, RequiresExplicitApproval)
VALUES
(@Id, @Prompt, @State, @PlanValidated, @HasUnresolvedAmbiguity, @AmbiguityMessage,
 @OverwriteRequested, @OverwriteAuthorized, @IsSimulated, @OutputPath, @CreatedUtc, @UpdatedUtc, @RequiresExplicitApproval);";
                BindJob(command, job);
                command.ExecuteNonQuery();
                if (image != null)
                {
                    using (var input = connection.CreateCommand())
                    {
                        input.Transaction = transaction;
                        input.CommandText = "INSERT INTO JobInputImages (JobId, MediaType, ImageBytes) VALUES (@JobId, @MediaType, @ImageBytes);";
                        Add(input, "@JobId", GuidText(job.Id));
                        Add(input, "@MediaType", image.MediaType);
                        Add(input, "@ImageBytes", image.Bytes);
                        input.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }

            return Task.CompletedTask;
        }

        public Task<CadPlanningImage> GetInputImageAsync(Guid jobId, CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MediaType, ImageBytes FROM JobInputImages WHERE JobId = @JobId;";
                Add(command, "@JobId", GuidText(jobId));
                using (var reader = command.ExecuteReader())
                    return Task.FromResult(reader.Read() ? new CadPlanningImage { MediaType = reader.GetString(0), Bytes = (byte[])reader.GetValue(1) } : null);
            }
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

        public Task<bool> HasNonTerminalJobsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT EXISTS(
    SELECT 1 FROM Jobs
    WHERE State NOT IN (@Completed, @Failed, @Cancelled)
);";
                Add(command, "@Completed", (int)JobState.Completed);
                Add(command, "@Failed", (int)JobState.Failed);
                Add(command, "@Cancelled", (int)JobState.Cancelled);
                return Task.FromResult(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0);
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
            CancellationToken cancellationToken = default(CancellationToken), Guid? expectedRevisionId = null)
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
WHERE Id = @Id AND State = @ExpectedState AND (@ExpectedRevision IS NULL OR (SELECT Id FROM Revisions WHERE JobId = @Id ORDER BY RevisionNumber DESC LIMIT 1) = @ExpectedRevision);";
                BindJob(command, job);
                Add(command, "@ExpectedState", (int)expectedState);
                Add(command, "@ExpectedRevision", expectedRevisionId.HasValue ? GuidText(expectedRevisionId.Value) : null);
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

        public Task<bool> TryAppendRevisionAsync(
            CadJob job,
            JobState expectedState,
            Guid expectedRevisionId,
            JobRevision revision,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateJob(job);
            if (expectedRevisionId == Guid.Empty) throw new ArgumentException("The expected revision ID is required.", nameof(expectedRevisionId));
            if (revision == null) throw new ArgumentNullException(nameof(revision));
            if (revision.JobId != job.Id) throw new ArgumentException("The revision must belong to the job being updated.", nameof(revision));
            if (revision.RevisionNumber < 1) throw new ArgumentException("The revision number must be positive.", nameof(revision));

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                JobState actualState;
                Guid actualRevisionId;
                using (var current = connection.CreateCommand())
                {
                    current.Transaction = transaction;
                    current.CommandText = @"
SELECT State,
       (SELECT Id FROM Revisions WHERE JobId = Jobs.Id ORDER BY RevisionNumber DESC LIMIT 1)
FROM Jobs WHERE Id = @Id;";
                    Add(current, "@Id", GuidText(job.Id));
                    using (var reader = current.ExecuteReader())
                    {
                        if (!reader.Read() || reader.IsDBNull(1))
                        {
                            transaction.Rollback();
                            return Task.FromResult(false);
                        }
                        actualState = (JobState)reader.GetInt32(0);
                        actualRevisionId = Guid.Parse(reader.GetString(1));
                    }
                }

                if (actualState != expectedState || actualRevisionId != expectedRevisionId)
                {
                    transaction.Rollback();
                    return Task.FromResult(false);
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = @"
INSERT INTO Revisions
(Id, JobId, RevisionNumber, Prompt, InterpretationJson, PlanJson, CreatedUtc)
VALUES
(@RevisionId, @JobId, @RevisionNumber, @RevisionPrompt, @InterpretationJson, @PlanJson, @RevisionCreatedUtc);";
                    Add(insert, "@RevisionId", GuidText(revision.Id));
                    Add(insert, "@JobId", GuidText(revision.JobId));
                    Add(insert, "@RevisionNumber", revision.RevisionNumber);
                    Add(insert, "@RevisionPrompt", revision.Prompt);
                    Add(insert, "@InterpretationJson", revision.InterpretationJson);
                    Add(insert, "@PlanJson", revision.PlanJson);
                    Add(insert, "@RevisionCreatedUtc", DateText(revision.CreatedUtc));
                    insert.ExecuteNonQuery();
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = @"
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
                    BindJob(update, job);
                    Add(update, "@ExpectedState", (int)expectedState);
                    if (update.ExecuteNonQuery() != 1)
                    {
                        transaction.Rollback();
                        return Task.FromResult(false);
                    }
                }

                transaction.Commit();
                return Task.FromResult(true);
            }
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

        public Task RegisterModelAsync(CadModelIdentityRecord model, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            ValidateModel(model);
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO ManagedModels
(ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
 CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc)
VALUES
(@ModelId, @ParentModelId, @DocumentKind, @Status, @CustomPropertyKey, @CanonicalPath, @LastSavedSha256,
 @CurrentModelRevisionId, @ConfigurationKey, @SolidWorksRevision, @RegistryVersion, @CreatedUtc, @UpdatedUtc);";
                    BindModel(command, model);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The model identity is already registered or violates a registry constraint.", exception);
            }
            return Task.CompletedTask;
        }

        public Task<CadModelIdentityRecord> GetModelAsync(Guid modelId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(modelId);
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256, CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc FROM ManagedModels WHERE ModelId = @ModelId;";
                Add(command, "@ModelId", GuidText(modelId));
                using (var reader = command.ExecuteReader())
                    return Task.FromResult(reader.Read() ? ReadModel(reader) : null);
            }
        }

        public Task<CadModelIdentityRecord> FindModelByCanonicalPathAsync(string canonicalPath, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            RequireText(canonicalPath, nameof(canonicalPath), 4096);
            var normalizedPath = Path.GetFullPath(canonicalPath);
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256, CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc FROM ManagedModels WHERE CanonicalPath = @CanonicalPath COLLATE NOCASE LIMIT 2;";
                Add(command, "@CanonicalPath", normalizedPath);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<CadModelIdentityRecord>(null);
                    var model = ReadModel(reader);
                    if (reader.Read())
                        throw new InvalidOperationException("More than one managed model is registered at the canonical path; refusing an ambiguous identity match.");
                    return Task.FromResult(model);
                }
            }
        }

        public Task UpdateModelAsync(CadModelIdentityRecord model, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            ValidateModel(model);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE ManagedModels SET ParentModelId = @ParentModelId, DocumentKind = @DocumentKind, Status = @Status,
 CustomPropertyKey = @CustomPropertyKey, CanonicalPath = @CanonicalPath, LastSavedSha256 = @LastSavedSha256,
 CurrentModelRevisionId = @CurrentModelRevisionId, ConfigurationKey = @ConfigurationKey,
 SolidWorksRevision = @SolidWorksRevision, RegistryVersion = @RegistryVersion, UpdatedUtc = @UpdatedUtc
WHERE ModelId = @ModelId;";
                BindModel(command, model);
                if (command.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException("The model identity is not registered: " + model.ModelId + ".");
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task AddEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            ValidateBinding(binding, false);
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO EntityReferenceBindings
(ModelId, EntityId, EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
 NativeReferenceBytes, CreatedAtModelRevisionId, LastResolvedModelRevisionId, SemanticFingerprintJson,
 Status, CreatedUtc, UpdatedUtc)
VALUES
(@ModelId, @EntityId, @EntityKind, @ConfigurationKey, @NativeObjectKind, @ReferenceFormatVersion,
 @NativeReferenceBytes, @CreatedAtModelRevisionId, @LastResolvedModelRevisionId, @SemanticFingerprintJson,
 @Status, @CreatedUtc, @UpdatedUtc);";
                    BindEntityBinding(command, binding, CloneBytes(binding.NativeReferenceBytes));
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The entity reference is already registered or its model is not registered.", exception);
            }
            return Task.CompletedTask;
        }

        public Task<CadEntityReferenceBinding> GetEntityBindingAsync(Guid modelId, Guid entityId, string configurationKey, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(modelId);
            GuidText(entityId);
            RequireText(configurationKey, nameof(configurationKey), 128);
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ModelId, EntityId, EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, LastResolvedModelRevisionId, SemanticFingerprintJson, Status, CreatedUtc, UpdatedUtc
FROM EntityReferenceBindings WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                Add(command, "@ModelId", GuidText(modelId));
                Add(command, "@EntityId", GuidText(entityId));
                Add(command, "@ConfigurationKey", configurationKey);
                using (var reader = command.ExecuteReader())
                    return Task.FromResult(reader.Read() ? ReadBinding(reader) : null);
            }
        }

        public Task UpdateEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            ValidateBinding(binding, true);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE EntityReferenceBindings SET EntityKind = @EntityKind, NativeObjectKind = @NativeObjectKind,
 ReferenceFormatVersion = @ReferenceFormatVersion, NativeReferenceBytes = COALESCE(@NativeReferenceBytes, NativeReferenceBytes),
 CreatedAtModelRevisionId = @CreatedAtModelRevisionId, LastResolvedModelRevisionId = @LastResolvedModelRevisionId,
 SemanticFingerprintJson = @SemanticFingerprintJson, Status = @Status, UpdatedUtc = @UpdatedUtc
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                BindEntityBinding(command, binding, CloneBytes(binding.NativeReferenceBytes));
                if (command.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException("The entity reference is not registered for this model and configuration.");
                transaction.Commit();
            }
            return Task.CompletedTask;
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
       OverwriteRequested, OverwriteAuthorized, IsSimulated, OutputPath, CreatedUtc, UpdatedUtc, RequiresExplicitApproval
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
                        UpdatedUtc = ParseDate(reader.GetString(11)),
                        RequiresExplicitApproval = reader.GetInt32(12) != 0
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
            Add(command, "@RequiresExplicitApproval", job.RequiresExplicitApproval ? 1 : 0);
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

        private static void EnsureSchemaVersion(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            var currentVersion = GetSchemaVersion(connection, transaction);
            if (currentVersion > 6)
                throw new InvalidOperationException("Database schema version " + currentVersion + " is newer than this Agent supports (6).");

            if (!HasColumn(connection, transaction, "Jobs", "RequiresExplicitApproval"))
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "ALTER TABLE Jobs ADD COLUMN RequiresExplicitApproval INTEGER NOT NULL DEFAULT 0;";
                    command.ExecuteNonQuery();
                }
            }
            if (!HasColumn(connection, transaction, "Jobs", "IsSimulated"))
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "ALTER TABLE Jobs ADD COLUMN IsSimulated INTEGER NOT NULL DEFAULT 0;";
                    command.ExecuteNonQuery();
                }
            }

            if (!HasColumn(connection, transaction, "V2ModelOwnerships", "FirstRevisionId") ||
                !HasColumn(connection, transaction, "V2MutationAttempts", "PlanSha256") ||
                !HasColumn(connection, transaction, "V2MutationAttempts", "ProspectiveModelRevisionId"))
                throw new InvalidOperationException("The v2 mutation provenance schema is incomplete or incompatible.");

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version = 6;";
                command.ExecuteNonQuery();
            }
        }

        private static long GetSchemaVersion(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static bool HasColumn(SQLiteConnection connection, SQLiteTransaction transaction, string table, string column)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
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

        private static void BindModel(SQLiteCommand command, CadModelIdentityRecord model)
        {
            Add(command, "@ModelId", GuidText(model.ModelId));
            Add(command, "@ParentModelId", model.ParentModelId.HasValue ? GuidText(model.ParentModelId.Value) : null);
            Add(command, "@DocumentKind", model.DocumentKind);
            Add(command, "@Status", model.Status.ToString());
            Add(command, "@CustomPropertyKey", model.CustomPropertyKey);
            Add(command, "@CanonicalPath", model.CanonicalPath);
            Add(command, "@LastSavedSha256", model.LastSavedSha256);
            Add(command, "@CurrentModelRevisionId", GuidText(model.CurrentModelRevisionId));
            Add(command, "@ConfigurationKey", model.ConfigurationKey);
            Add(command, "@SolidWorksRevision", model.SolidWorksRevision);
            Add(command, "@RegistryVersion", model.RegistryVersion);
            Add(command, "@CreatedUtc", DateText(model.CreatedUtc));
            Add(command, "@UpdatedUtc", DateText(model.UpdatedUtc));
        }

        private static CadModelIdentityRecord ReadModel(SQLiteDataReader reader)
        {
            return new CadModelIdentityRecord
            {
                ModelId = Guid.Parse(reader.GetString(0)),
                ParentModelId = reader.IsDBNull(1) ? (Guid?)null : Guid.Parse(reader.GetString(1)),
                DocumentKind = reader.GetString(2),
                Status = ParseEnum<CadModelIdentityStatus>(reader.GetString(3), "model identity status"),
                CustomPropertyKey = reader.GetString(4),
                CanonicalPath = NullableString(reader, 5),
                LastSavedSha256 = NullableString(reader, 6),
                CurrentModelRevisionId = Guid.Parse(reader.GetString(7)),
                ConfigurationKey = reader.GetString(8),
                SolidWorksRevision = NullableString(reader, 9),
                RegistryVersion = reader.GetInt32(10),
                CreatedUtc = ParseDate(reader.GetString(11)),
                UpdatedUtc = ParseDate(reader.GetString(12))
            };
        }

        private static void BindEntityBinding(SQLiteCommand command, CadEntityReferenceBinding binding, byte[] bytes)
        {
            Add(command, "@ModelId", GuidText(binding.ModelId));
            Add(command, "@EntityId", GuidText(binding.EntityId));
            Add(command, "@EntityKind", binding.EntityKind);
            Add(command, "@ConfigurationKey", binding.ConfigurationKey);
            Add(command, "@NativeObjectKind", binding.NativeObjectKind);
            Add(command, "@ReferenceFormatVersion", binding.ReferenceFormatVersion);
            Add(command, "@NativeReferenceBytes", bytes);
            Add(command, "@CreatedAtModelRevisionId", GuidText(binding.CreatedAtModelRevisionId));
            Add(command, "@LastResolvedModelRevisionId", binding.LastResolvedModelRevisionId.HasValue ? GuidText(binding.LastResolvedModelRevisionId.Value) : null);
            Add(command, "@SemanticFingerprintJson", binding.SemanticFingerprintJson);
            Add(command, "@Status", binding.Status.ToString());
            Add(command, "@CreatedUtc", DateText(binding.CreatedUtc));
            Add(command, "@UpdatedUtc", DateText(binding.UpdatedUtc));
        }

        private static CadEntityReferenceBinding ReadBinding(SQLiteDataReader reader)
        {
            return new CadEntityReferenceBinding
            {
                ModelId = Guid.Parse(reader.GetString(0)),
                EntityId = Guid.Parse(reader.GetString(1)),
                EntityKind = reader.GetString(2),
                ConfigurationKey = reader.GetString(3),
                NativeObjectKind = reader.GetString(4),
                ReferenceFormatVersion = reader.GetInt32(5),
                NativeReferenceBytes = reader.IsDBNull(6) ? null : CloneBytes((byte[])reader.GetValue(6)),
                CreatedAtModelRevisionId = Guid.Parse(reader.GetString(7)),
                LastResolvedModelRevisionId = reader.IsDBNull(8) ? (Guid?)null : Guid.Parse(reader.GetString(8)),
                SemanticFingerprintJson = NullableString(reader, 9),
                Status = ParseEnum<CadEntityReferenceStatus>(reader.GetString(10), "entity reference status"),
                CreatedUtc = ParseDate(reader.GetString(11)),
                UpdatedUtc = ParseDate(reader.GetString(12))
            };
        }

        private static TEnum ParseEnum<TEnum>(string value, string description) where TEnum : struct
        {
            TEnum parsed;
            if (!Enum.TryParse(value, false, out parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
                throw new InvalidOperationException("The stored " + description + " is not supported: " + value + ".");
            return parsed;
        }

        private static void ValidateModel(CadModelIdentityRecord model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            GuidText(model.ModelId);
            if (model.ParentModelId.HasValue) GuidText(model.ParentModelId.Value);
            RequireText(model.DocumentKind, nameof(model.DocumentKind), 64);
            if (!Enum.IsDefined(typeof(CadModelIdentityStatus), model.Status))
                throw new ArgumentOutOfRangeException(nameof(model.Status));
            RequireText(model.CustomPropertyKey, nameof(model.CustomPropertyKey), 128);
            if (model.CanonicalPath != null) RequireText(model.CanonicalPath, nameof(model.CanonicalPath), 4096);
            if (model.LastSavedSha256 != null) RequireText(model.LastSavedSha256, nameof(model.LastSavedSha256), 128);
            GuidText(model.CurrentModelRevisionId);
            RequireText(model.ConfigurationKey, nameof(model.ConfigurationKey), 128);
            if (model.SolidWorksRevision != null) RequireText(model.SolidWorksRevision, nameof(model.SolidWorksRevision), 128);
            if (model.RegistryVersion <= 0) throw new ArgumentOutOfRangeException(nameof(model.RegistryVersion));
            DateText(model.CreatedUtc);
            DateText(model.UpdatedUtc);
        }

        private static void ValidateBinding(CadEntityReferenceBinding binding, bool updating)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            GuidText(binding.ModelId);
            GuidText(binding.EntityId);
            RequireText(binding.EntityKind, nameof(binding.EntityKind), 128);
            RequireText(binding.ConfigurationKey, nameof(binding.ConfigurationKey), 128);
            RequireText(binding.NativeObjectKind, nameof(binding.NativeObjectKind), 128);
            if (binding.ReferenceFormatVersion <= 0) throw new ArgumentOutOfRangeException(nameof(binding.ReferenceFormatVersion));
            if (!Enum.IsDefined(typeof(CadEntityReferenceStatus), binding.Status))
                throw new ArgumentOutOfRangeException(nameof(binding.Status));
            GuidText(binding.CreatedAtModelRevisionId);
            if (binding.LastResolvedModelRevisionId.HasValue) GuidText(binding.LastResolvedModelRevisionId.Value);
            DateText(binding.CreatedUtc);
            DateText(binding.UpdatedUtc);
            if (binding.NativeReferenceBytes != null && (binding.NativeReferenceBytes.Length == 0 || binding.NativeReferenceBytes.Length > MaximumNativeReferenceBytes))
                throw new ArgumentException("Native reference bytes must contain between 1 and " + MaximumNativeReferenceBytes + " bytes.", nameof(binding.NativeReferenceBytes));
            if (!updating && binding.Status == CadEntityReferenceStatus.Active && binding.NativeReferenceBytes == null)
                throw new ArgumentException("An active entity reference requires native reference bytes.", nameof(binding.NativeReferenceBytes));
            if (binding.SemanticFingerprintJson != null && binding.SemanticFingerprintJson.Length > MaximumSemanticFingerprintJsonLength)
                throw new ArgumentException("The semantic fingerprint exceeds the supported length.", nameof(binding.SemanticFingerprintJson));
        }

        private const int MaximumNativeReferenceBytes = 1024 * 1024;
        private const int MaximumSemanticFingerprintJsonLength = 256 * 1024;

        private static string RequireText(string value, string name, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", name);
            if (value.Length > maximumLength) throw new ArgumentException("The value exceeds the supported length of " + maximumLength + ".", name);
            return value;
        }

        private static byte[] CloneBytes(byte[] bytes) => bytes == null ? null : (byte[])bytes.Clone();

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteJobRepository));
        }
    }
}
