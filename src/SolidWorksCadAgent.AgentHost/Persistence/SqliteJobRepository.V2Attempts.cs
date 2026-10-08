using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository
    {
        /// <summary>
        /// Atomically reserves a fresh managed model for the current persisted v2 NewPart step.
        /// This is provenance storage only; it does not approve or execute the plan.
        /// </summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2NewPartAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, CadModelIdentityRecord model, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId);
            GuidText(revisionId);
            ValidateModel(model);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            if (model.Status != CadModelIdentityStatus.Pending || model.ParentModelId.HasValue ||
                !string.Equals(model.DocumentKind, "Part", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                throw new ArgumentException("A fresh pending part identity is required.", nameof(model));

            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    string planJson;
                    using (var current = connection.CreateCommand())
                    {
                        current.Transaction = transaction;
                        current.CommandText = @"
SELECT r.PlanJson FROM Revisions r JOIN Jobs j ON j.Id = r.JobId
WHERE r.JobId = @JobId AND r.Id = @RevisionId AND j.IsSimulated = 0
  AND j.PlanValidated = 1 AND j.State = @Executing
  AND r.Id = (SELECT Id FROM Revisions WHERE JobId = @JobId ORDER BY RevisionNumber DESC LIMIT 1);";
                        Add(current, "@JobId", GuidText(jobId));
                        Add(current, "@RevisionId", GuidText(revisionId));
                        Add(current, "@Executing", (int)JobState.Executing);
                        planJson = current.ExecuteScalar() as string;
                    }
                    var parsed = CadPlanDocumentReader.ReadPersisted(planJson);
                    if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null ||
                        parsed.CandidateV2.Steps.Count == 0 ||
                        !string.Equals(parsed.CandidateV2.Steps[0].StepKey, stepKey, StringComparison.Ordinal) ||
                        !string.Equals(parsed.CandidateV2.Steps[0].Command, CadCommandNames.NewPart, StringComparison.Ordinal))
                        throw new InvalidOperationException("The current stored revision must begin with the specified normalized v2 NewPart step.");

                    var now = DateTime.UtcNow;
                    var attempt = new CadV2MutationAttemptRecord(Guid.NewGuid(), jobId, revisionId,
                        HashPlan(planJson), stepKey, model.ModelId, null, model.CurrentModelRevisionId,
                        CadV2MutationAttemptStatus.Prepared);
                    using (var insertModel = connection.CreateCommand())
                    {
                        insertModel.Transaction = transaction;
                        insertModel.CommandText = @"
INSERT INTO ManagedModels
(ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
 CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc)
VALUES
(@ModelId, @ParentModelId, @DocumentKind, @Status, @CustomPropertyKey, @CanonicalPath, @LastSavedSha256,
 @CurrentModelRevisionId, @ConfigurationKey, @SolidWorksRevision, @RegistryVersion, @CreatedUtc, @UpdatedUtc);";
                        BindModel(insertModel, model);
                        insertModel.ExecuteNonQuery();
                    }
                    using (var owner = connection.CreateCommand())
                    {
                        owner.Transaction = transaction;
                        owner.CommandText = "INSERT INTO V2ModelOwnerships (ModelId, JobId, FirstRevisionId, CreatedUtc) VALUES (@ModelId, @JobId, @RevisionId, @Now);";
                        Add(owner, "@ModelId", GuidText(model.ModelId));
                        Add(owner, "@JobId", GuidText(jobId));
                        Add(owner, "@RevisionId", GuidText(revisionId));
                        Add(owner, "@Now", DateText(now));
                        owner.ExecuteNonQuery();
                    }
                    using (var insertAttempt = connection.CreateCommand())
                    {
                        insertAttempt.Transaction = transaction;
                        insertAttempt.CommandText = @"
INSERT INTO V2MutationAttempts
(Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId,
 ProspectiveModelRevisionId, Status, CreatedUtc, UpdatedUtc)
VALUES (@Id, @JobId, @RevisionId, @PlanSha256, @StepKey, @ModelId, NULL,
 @ProspectiveModelRevisionId, 'Prepared', @Now, @Now);";
                        Add(insertAttempt, "@Id", GuidText(attempt.Id));
                        Add(insertAttempt, "@JobId", GuidText(jobId));
                        Add(insertAttempt, "@RevisionId", GuidText(revisionId));
                        Add(insertAttempt, "@PlanSha256", attempt.PlanSha256);
                        Add(insertAttempt, "@StepKey", stepKey);
                        Add(insertAttempt, "@ModelId", GuidText(model.ModelId));
                        Add(insertAttempt, "@ProspectiveModelRevisionId", GuidText(model.CurrentModelRevisionId));
                        Add(insertAttempt, "@Now", DateText(now));
                        insertAttempt.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return Task.FromResult(attempt);
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The v2 model or NewPart attempt is already reserved or violates ownership constraints.", exception);
            }
        }

        /// <summary>
        /// Commits the successful Host observation of a managed NewPart. The Bridge result
        /// includes modelId only after the native custom property has been read back and verified.
        /// Model activation and the attempt transition commit atomically.
        /// </summary>
        public Task CommitV2NewPartOutcomeAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid modelId,
            CadCommandResult observedResult, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId);
            GuidText(revisionId);
            GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            if (observedResult == null || !observedResult.Success || observedResult.Data == null)
                throw new InvalidOperationException("Only a successful observed NewPart result can be committed.");
            var observedModelIdText = observedResult.Data.Value<string>("modelId");
            var title = observedResult.Data.Value<string>("documentTitle");
            var configurationKey = observedResult.Data.Value<string>("configurationKey");
            if (!Guid.TryParse(observedModelIdText, out var observedModelId) || observedModelId != modelId ||
                string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(configurationKey) || configurationKey.Length > 128)
                throw new InvalidOperationException("The successful NewPart result is missing matching model identity, document title or configuration evidence.");

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                string planJson;
                using (var current = connection.CreateCommand())
                {
                    current.Transaction = transaction;
                    current.CommandText = @"
SELECT r.PlanJson FROM Revisions r JOIN Jobs j ON j.Id = r.JobId
WHERE r.JobId = @JobId AND r.Id = @RevisionId AND j.IsSimulated = 0
  AND j.PlanValidated = 1 AND j.State = @Executing
  AND r.Id = (SELECT Id FROM Revisions WHERE JobId = @JobId ORDER BY RevisionNumber DESC LIMIT 1);";
                    Add(current, "@JobId", GuidText(jobId));
                    Add(current, "@RevisionId", GuidText(revisionId));
                    Add(current, "@Executing", (int)JobState.Executing);
                    planJson = current.ExecuteScalar() as string;
                }
                if (string.IsNullOrWhiteSpace(planJson))
                    throw new InvalidOperationException("The NewPart result does not belong to the current executing job revision.");

                var attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey);
                if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Prepared ||
                    attempt.ModelId != modelId || !string.Equals(attempt.PlanSha256, HashPlan(planJson), StringComparison.Ordinal))
                    throw new InvalidOperationException("The NewPart result does not match a Prepared attempt for the current stored plan and owned model.");

                var parsed = CadPlanDocumentReader.ReadPersisted(planJson);
                if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null ||
                    parsed.CandidateV2.Steps.Count == 0 ||
                    !string.Equals(parsed.CandidateV2.Steps[0].StepKey, stepKey, StringComparison.Ordinal) ||
                    !string.Equals(parsed.CandidateV2.Steps[0].Command, CadCommandNames.NewPart, StringComparison.Ordinal))
                    throw new InvalidOperationException("The current stored plan no longer contains this normalized NewPart step.");

                using (var owner = connection.CreateCommand())
                {
                    owner.Transaction = transaction;
                    owner.CommandText = "SELECT COUNT(*) FROM V2ModelOwnerships WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                    Add(owner, "@ModelId", GuidText(modelId));
                    Add(owner, "@JobId", GuidText(jobId));
                    Add(owner, "@RevisionId", GuidText(revisionId));
                    if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("The managed model is not owned by this job and first revision.");
                }

                using (var updateModel = connection.CreateCommand())
                {
                    updateModel.Transaction = transaction;
                    updateModel.CommandText = @"
UPDATE ManagedModels SET Status = @Active, ConfigurationKey = @ConfigurationKey, UpdatedUtc = @Now
WHERE ModelId = @ModelId AND Status = @Pending AND CurrentModelRevisionId = @ModelRevisionId
  AND CanonicalPath IS NULL AND LastSavedSha256 IS NULL;";
                    Add(updateModel, "@Active", CadModelIdentityStatus.ActiveUnsaved.ToString());
                    Add(updateModel, "@ConfigurationKey", configurationKey);
                    Add(updateModel, "@Now", DateText(DateTime.UtcNow));
                    Add(updateModel, "@ModelId", GuidText(modelId));
                    Add(updateModel, "@Pending", CadModelIdentityStatus.Pending.ToString());
                    Add(updateModel, "@ModelRevisionId", GuidText(attempt.ProspectiveModelRevisionId));
                    if (updateModel.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("The managed model is no longer in its expected pending state.");
                }
                using (var updateAttempt = connection.CreateCommand())
                {
                    updateAttempt.Transaction = transaction;
                    updateAttempt.CommandText = "UPDATE V2MutationAttempts SET Status = 'Applied', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                    Add(updateAttempt, "@Now", DateText(DateTime.UtcNow));
                    Add(updateAttempt, "@Id", GuidText(attempt.Id));
                    if (updateAttempt.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("The NewPart attempt is no longer prepared.");
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task<Guid?> GetV2ModelOwnerAsync(Guid modelId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT JobId FROM V2ModelOwnerships WHERE ModelId = @ModelId;";
                Add(command, "@ModelId", GuidText(modelId));
                var value = command.ExecuteScalar() as string;
                return Task.FromResult(value == null ? (Guid?)null : Guid.Parse(value));
            }
        }

        public Task<CadV2MutationAttemptRecord> GetV2MutationAttemptAsync(Guid attemptId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId, ProspectiveModelRevisionId, Status
FROM V2MutationAttempts WHERE Id = @Id;";
                Add(command, "@Id", GuidText(attemptId));
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<CadV2MutationAttemptRecord>(null);
                    return Task.FromResult(ReadV2MutationAttempt(reader));
                }
            }
        }

        /// <summary>Finds a prepared or uncertain attempt after restart without an in-memory attempt ID.</summary>
        public Task<CadV2MutationAttemptRecord> FindV2MutationAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId, ProspectiveModelRevisionId, Status
FROM V2MutationAttempts WHERE JobId = @JobId AND RevisionId = @RevisionId AND StepKey = @StepKey;";
                Add(command, "@JobId", GuidText(jobId));
                Add(command, "@RevisionId", GuidText(revisionId));
                Add(command, "@StepKey", stepKey);
                using (var reader = command.ExecuteReader())
                    return Task.FromResult(reader.Read() ? ReadV2MutationAttempt(reader) : null);
            }
        }

        public Task MarkV2MutationAttemptUncertainAsync(Guid attemptId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var now = DateText(DateTime.UtcNow);
                using (var model = connection.CreateCommand())
                {
                    model.Transaction = transaction;
                    model.CommandText = @"
UPDATE ManagedModels SET Status = @Uncertain, UpdatedUtc = @Now
WHERE ModelId = (SELECT ModelId FROM V2MutationAttempts WHERE Id = @Id AND Status = 'Prepared');";
                    Add(model, "@Uncertain", CadModelIdentityStatus.Uncertain.ToString());
                    Add(model, "@Now", now);
                    Add(model, "@Id", GuidText(attemptId));
                    model.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE V2MutationAttempts SET Status = 'Uncertain', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                    Add(command, "@Id", GuidText(attemptId));
                    Add(command, "@Now", now);
                    if (command.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Only a prepared v2 attempt can become uncertain.");
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        private static string HashPlan(string planJson)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(planJson)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static CadV2MutationAttemptRecord ReadV2MutationAttempt(SQLiteDataReader reader)
        {
            return new CadV2MutationAttemptRecord(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? (Guid?)null : Guid.Parse(reader.GetString(6)), Guid.Parse(reader.GetString(7)),
                (CadV2MutationAttemptStatus)Enum.Parse(typeof(CadV2MutationAttemptStatus), reader.GetString(8), false));
        }

        private static CadV2MutationAttemptRecord FindV2MutationAttemptInTransaction(
            SQLiteConnection connection, SQLiteTransaction transaction, Guid jobId, Guid revisionId, string stepKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId, ProspectiveModelRevisionId, Status
FROM V2MutationAttempts WHERE JobId = @JobId AND RevisionId = @RevisionId AND StepKey = @StepKey;";
                Add(command, "@JobId", GuidText(jobId));
                Add(command, "@RevisionId", GuidText(revisionId));
                Add(command, "@StepKey", stepKey);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? ReadV2MutationAttempt(reader) : null;
            }
        }
    }
}
