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
        /// Atomically reserves the first managed sketch output for the current normalized v2
        /// plan. This records intent only; the model revision is advanced only after a later
        /// observed native result is committed.
        /// </summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2SketchAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid modelId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId);
            GuidText(revisionId);
            GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));

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
                        parsed.CandidateV2.Steps.Count < 2)
                        throw new InvalidOperationException("A current normalized v2 plan with NewPart followed by CreateSketch is required.");

                    var steps = parsed.CandidateV2.Steps;
                    var newPartStep = steps[0];
                    var sketchStep = steps[1];
                    if (!string.Equals(newPartStep.Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                        !string.Equals(sketchStep.StepKey, stepKey, StringComparison.Ordinal) ||
                        !string.Equals(sketchStep.Command, CadCommandNames.CreateSketch, StringComparison.Ordinal) ||
                        sketchStep.OperationVersion != 1 || !sketchStep.OutputEntityId.HasValue)
                        throw new InvalidOperationException("Only the exact first normalized CreateSketch output immediately after NewPart can be prepared by this slice.");

                    var now = DateTime.UtcNow;
                    CadV2MutationAttemptRecord newPartAttempt;
                    using (var previous = connection.CreateCommand())
                    {
                        previous.Transaction = transaction;
                        previous.CommandText = @"
SELECT Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId, ProspectiveModelRevisionId, Status
FROM V2MutationAttempts WHERE JobId = @JobId AND RevisionId = @RevisionId AND StepKey = @NewPartStepKey;";
                        Add(previous, "@JobId", GuidText(jobId));
                        Add(previous, "@RevisionId", GuidText(revisionId));
                        Add(previous, "@NewPartStepKey", newPartStep.StepKey);
                        using (var reader = previous.ExecuteReader())
                            newPartAttempt = reader.Read() ? ReadV2MutationAttempt(reader) : null;
                    }
                    if (newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue ||
                        !string.Equals(newPartAttempt.PlanSha256, HashPlan(planJson), StringComparison.Ordinal))
                        throw new InvalidOperationException("The exact NewPart attempt for this job and revision must already be Applied to the requested model.");

                    CadModelIdentityRecord model;
                    using (var currentModel = connection.CreateCommand())
                    {
                        currentModel.Transaction = transaction;
                        currentModel.CommandText = @"
SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
 CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc
FROM ManagedModels WHERE ModelId = @ModelId;";
                        Add(currentModel, "@ModelId", GuidText(modelId));
                        using (var reader = currentModel.ExecuteReader())
                            model = reader.Read() ? ReadModel(reader) : null;
                    }
                    if (model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                        model.CurrentModelRevisionId != newPartAttempt.ProspectiveModelRevisionId ||
                        !string.Equals(model.DocumentKind, "Part", StringComparison.Ordinal) ||
                        !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                        throw new InvalidOperationException("The applied NewPart must own an active unsaved part at its committed model revision.");

                    using (var owner = connection.CreateCommand())
                    {
                        owner.Transaction = transaction;
                        owner.CommandText = @"SELECT COUNT(*) FROM V2ModelOwnerships
WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                        Add(owner, "@ModelId", GuidText(modelId));
                        Add(owner, "@JobId", GuidText(jobId));
                        Add(owner, "@RevisionId", GuidText(revisionId));
                        if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                            throw new InvalidOperationException("The managed part is not owned by this job and revision.");
                    }

                    var attempt = new CadV2MutationAttemptRecord(Guid.NewGuid(), jobId, revisionId,
                        HashPlan(planJson), stepKey, modelId, sketchStep.OutputEntityId.Value, Guid.NewGuid(),
                        CadV2MutationAttemptStatus.Prepared);
                    using (var insertAttempt = connection.CreateCommand())
                    {
                        insertAttempt.Transaction = transaction;
                        insertAttempt.CommandText = @"
INSERT INTO V2MutationAttempts
(Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId,
 ProspectiveModelRevisionId, Status, CreatedUtc, UpdatedUtc)
VALUES (@Id, @JobId, @RevisionId, @PlanSha256, @StepKey, @ModelId, @OutputEntityId,
 @ProspectiveModelRevisionId, 'Prepared', @Now, @Now);";
                        Add(insertAttempt, "@Id", GuidText(attempt.Id));
                        Add(insertAttempt, "@JobId", GuidText(jobId));
                        Add(insertAttempt, "@RevisionId", GuidText(revisionId));
                        Add(insertAttempt, "@PlanSha256", attempt.PlanSha256);
                        Add(insertAttempt, "@StepKey", stepKey);
                        Add(insertAttempt, "@ModelId", GuidText(modelId));
                        Add(insertAttempt, "@OutputEntityId", GuidText(attempt.OutputEntityId.Value));
                        Add(insertAttempt, "@ProspectiveModelRevisionId", GuidText(attempt.ProspectiveModelRevisionId));
                        Add(insertAttempt, "@Now", DateText(now));
                        insertAttempt.ExecuteNonQuery();
                    }
                    using (var insertBinding = connection.CreateCommand())
                    {
                        insertBinding.Transaction = transaction;
                        insertBinding.CommandText = @"
INSERT INTO EntityReferenceBindings
(ModelId, EntityId, EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
 NativeReferenceBytes, CreatedAtModelRevisionId, LastResolvedModelRevisionId, SemanticFingerprintJson,
 Status, CreatedUtc, UpdatedUtc)
VALUES (@ModelId, @EntityId, 'Sketch', @ConfigurationKey, 'SketchFeature', 3,
 NULL, @ModelRevisionId, NULL, NULL, 'Pending', @Now, @Now);";
                        Add(insertBinding, "@ModelId", GuidText(modelId));
                        Add(insertBinding, "@EntityId", GuidText(attempt.OutputEntityId.Value));
                        Add(insertBinding, "@ConfigurationKey", model.ConfigurationKey);
                        Add(insertBinding, "@ModelRevisionId", GuidText(attempt.ProspectiveModelRevisionId));
                        Add(insertBinding, "@Now", DateText(now));
                        insertBinding.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return Task.FromResult(attempt);
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The v2 sketch attempt or pending entity binding is already reserved.", exception);
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

        /// <summary>
        /// Commits a successful managed CreateSketch dispatch only when the Bridge has already
        /// persisted the exact output's native persistent reference. Model revision advancement
        /// and attempt application are one SQLite transaction.
        /// </summary>
        public Task CommitV2SketchOutcomeAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid modelId,
            CadCommandResult observedResult, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId);
            GuidText(revisionId);
            GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            if (observedResult == null || !observedResult.Success || observedResult.Data == null ||
                !Guid.TryParse(observedResult.Data.Value<string>("entityId"), out var observedEntityId))
                throw new InvalidOperationException("Only a successful observed CreateSketch result with an entity identity can be committed.");

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
                    throw new InvalidOperationException("The CreateSketch result does not belong to the current executing job revision.");

                var parsed = CadPlanDocumentReader.ReadPersisted(planJson);
                if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null || parsed.CandidateV2.Steps.Count < 2 ||
                    !string.Equals(parsed.CandidateV2.Steps[0].Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                    !string.Equals(parsed.CandidateV2.Steps[1].StepKey, stepKey, StringComparison.Ordinal) ||
                    !string.Equals(parsed.CandidateV2.Steps[1].Command, CadCommandNames.CreateSketch, StringComparison.Ordinal) ||
                    parsed.CandidateV2.Steps[1].OutputEntityId != observedEntityId)
                    throw new InvalidOperationException("The observed sketch identity does not match the exact second normalized plan step.");

                var attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey);
                if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Prepared || attempt.ModelId != modelId ||
                    attempt.OutputEntityId != observedEntityId || !string.Equals(attempt.PlanSha256, HashPlan(planJson), StringComparison.Ordinal))
                    throw new InvalidOperationException("The CreateSketch result does not match the exact Prepared attempt and current stored plan.");

                CadModelIdentityRecord model;
                using (var currentModel = connection.CreateCommand())
                {
                    currentModel.Transaction = transaction;
                    currentModel.CommandText = @"SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
 CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc
FROM ManagedModels WHERE ModelId = @ModelId;";
                    Add(currentModel, "@ModelId", GuidText(modelId));
                    using (var reader = currentModel.ExecuteReader()) model = reader.Read() ? ReadModel(reader) : null;
                }
                if (model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                    model.CurrentModelRevisionId == attempt.ProspectiveModelRevisionId ||
                    !string.Equals(model.DocumentKind, "Part", StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                    throw new InvalidOperationException("The managed part is not at the expected pre-sketch ActiveUnsaved model revision.");

                var newPartAttempt = FindV2MutationAttemptInTransaction(
                    connection, transaction, jobId, revisionId, parsed.CandidateV2.Steps[0].StepKey);
                if (newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    newPartAttempt.ModelId != modelId ||
                    !string.Equals(newPartAttempt.PlanSha256, attempt.PlanSha256, StringComparison.Ordinal) ||
                    model.CurrentModelRevisionId != newPartAttempt.ProspectiveModelRevisionId)
                    throw new InvalidOperationException("The applied NewPart attempt must match the managed model's current pre-sketch revision.");

                using (var owner = connection.CreateCommand())
                {
                    owner.Transaction = transaction;
                    owner.CommandText = "SELECT COUNT(*) FROM V2ModelOwnerships WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                    Add(owner, "@ModelId", GuidText(modelId));
                    Add(owner, "@JobId", GuidText(jobId));
                    Add(owner, "@RevisionId", GuidText(revisionId));
                    if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("The managed part is not owned by this job and first revision.");
                }

                using (var binding = connection.CreateCommand())
                {
                    binding.Transaction = transaction;
                    binding.CommandText = @"SELECT EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, Status FROM EntityReferenceBindings
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                    Add(binding, "@ModelId", GuidText(modelId));
                    Add(binding, "@EntityId", GuidText(observedEntityId));
                    Add(binding, "@ConfigurationKey", model.ConfigurationKey);
                    using (var reader = binding.ExecuteReader())
                    {
                        var referenceBytes = reader.Read() && !reader.IsDBNull(4) ? (byte[])reader[4] : null;
                        if (referenceBytes == null || referenceBytes.Length == 0 || referenceBytes.Length > 1024 * 1024 ||
                            reader.GetString(0) != "Sketch" || reader.GetString(1) != model.ConfigurationKey ||
                            reader.GetString(2) != "SketchFeature" || reader.GetInt32(3) != 3 ||
                            Guid.Parse(reader.GetString(5)) != attempt.ProspectiveModelRevisionId ||
                            reader.GetString(6) != CadEntityReferenceStatus.Active.ToString())
                            throw new InvalidOperationException("The Bridge has not persisted an active, bounded native reference for this exact sketch output.");
                    }
                }

                using (var updateModel = connection.CreateCommand())
                {
                    updateModel.Transaction = transaction;
                    updateModel.CommandText = @"UPDATE ManagedModels SET CurrentModelRevisionId = @NextRevision, UpdatedUtc = @Now
WHERE ModelId = @ModelId AND Status = @Active AND CurrentModelRevisionId = @PreviousRevision;";
                    Add(updateModel, "@NextRevision", GuidText(attempt.ProspectiveModelRevisionId));
                    Add(updateModel, "@Now", DateText(DateTime.UtcNow));
                    Add(updateModel, "@ModelId", GuidText(modelId));
                    Add(updateModel, "@Active", CadModelIdentityStatus.ActiveUnsaved.ToString());
                    Add(updateModel, "@PreviousRevision", GuidText(model.CurrentModelRevisionId));
                    if (updateModel.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("The managed model revision changed before the sketch result could commit.");
                }
                using (var updateAttempt = connection.CreateCommand())
                {
                    updateAttempt.Transaction = transaction;
                    updateAttempt.CommandText = "UPDATE V2MutationAttempts SET Status = 'Applied', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                    Add(updateAttempt, "@Now", DateText(DateTime.UtcNow));
                    Add(updateAttempt, "@Id", GuidText(attempt.Id));
                    if (updateAttempt.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("The CreateSketch attempt is no longer prepared.");
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reserves the exact first rectangle after a committed sketch in a normalized v2 plan.
        /// This is persistence-only: a later dispatch and observed result must commit the
        /// prospective revision. A rectangle produces no logical output entity binding.
        /// </summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2RectangleAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid modelId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId);
            GuidText(revisionId);
            GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));

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
                        parsed.CandidateV2.Steps.Count < 3)
                        throw new InvalidOperationException("A current normalized v2 plan with NewPart, CreateSketch and AddRectangle is required.");

                    var steps = parsed.CandidateV2.Steps;
                    var newPartStep = steps[0];
                    var sketchStep = steps[1];
                    var rectangleStep = steps[2];
                    if (!string.Equals(newPartStep.Command, CadCommandNames.NewPart, StringComparison.Ordinal) ||
                        !string.Equals(sketchStep.Command, CadCommandNames.CreateSketch, StringComparison.Ordinal) ||
                        sketchStep.OperationVersion != 1 || !sketchStep.OutputEntityId.HasValue ||
                        !string.Equals(rectangleStep.StepKey, stepKey, StringComparison.Ordinal) ||
                        !string.Equals(rectangleStep.Command, CadCommandNames.AddRectangle, StringComparison.Ordinal) ||
                        rectangleStep.OperationVersion != 1 || rectangleStep.OutputEntityId.HasValue)
                        throw new InvalidOperationException("Only the exact third normalized AddRectangle after NewPart and CreateSketch can be prepared by this slice.");

                    var planHash = HashPlan(planJson);
                    var sketchAttempt = FindV2MutationAttemptInTransaction(
                        connection, transaction, jobId, revisionId, sketchStep.StepKey);
                    if (sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        sketchAttempt.ModelId != modelId || sketchAttempt.OutputEntityId != sketchStep.OutputEntityId ||
                        !string.Equals(sketchAttempt.PlanSha256, planHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("The exact preceding CreateSketch attempt must be Applied to this model and plan.");

                    var newPartAttempt = FindV2MutationAttemptInTransaction(
                        connection, transaction, jobId, revisionId, newPartStep.StepKey);
                    if (newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue ||
                        !string.Equals(newPartAttempt.PlanSha256, planHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("The preceding NewPart attempt must be Applied to this model and plan.");

                    CadModelIdentityRecord model;
                    using (var currentModel = connection.CreateCommand())
                    {
                        currentModel.Transaction = transaction;
                        currentModel.CommandText = @"SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
 CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc
FROM ManagedModels WHERE ModelId = @ModelId;";
                        Add(currentModel, "@ModelId", GuidText(modelId));
                        using (var reader = currentModel.ExecuteReader()) model = reader.Read() ? ReadModel(reader) : null;
                    }
                    if (model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved ||
                        model.CurrentModelRevisionId != sketchAttempt.ProspectiveModelRevisionId ||
                        !string.Equals(model.DocumentKind, "Part", StringComparison.Ordinal) ||
                        !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                        throw new InvalidOperationException("The applied sketch must own the current active unsaved part revision.");

                    using (var owner = connection.CreateCommand())
                    {
                        owner.Transaction = transaction;
                        owner.CommandText = @"SELECT COUNT(*) FROM V2ModelOwnerships
WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                        Add(owner, "@ModelId", GuidText(modelId));
                        Add(owner, "@JobId", GuidText(jobId));
                        Add(owner, "@RevisionId", GuidText(revisionId));
                        if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                            throw new InvalidOperationException("The managed part is not owned by this job and revision.");
                    }

                    using (var binding = connection.CreateCommand())
                    {
                        binding.Transaction = transaction;
                        binding.CommandText = @"SELECT EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, Status FROM EntityReferenceBindings
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                        Add(binding, "@ModelId", GuidText(modelId));
                        Add(binding, "@EntityId", GuidText(sketchStep.OutputEntityId.Value));
                        Add(binding, "@ConfigurationKey", model.ConfigurationKey);
                        using (var reader = binding.ExecuteReader())
                        {
                            var referenceBytes = reader.Read() && !reader.IsDBNull(4) ? (byte[])reader[4] : null;
                            if (referenceBytes == null || referenceBytes.Length == 0 || referenceBytes.Length > 1024 * 1024 ||
                                reader.GetString(0) != "Sketch" || reader.GetString(1) != model.ConfigurationKey ||
                                reader.GetString(2) != "SketchFeature" || reader.GetInt32(3) != 3 ||
                                Guid.Parse(reader.GetString(5)) != sketchAttempt.ProspectiveModelRevisionId ||
                                reader.GetString(6) != CadEntityReferenceStatus.Active.ToString())
                                throw new InvalidOperationException("The committed sketch has no active, bounded native reference.");
                        }
                    }

                    var attempt = new CadV2MutationAttemptRecord(Guid.NewGuid(), jobId, revisionId,
                        planHash, stepKey, modelId, null, Guid.NewGuid(), CadV2MutationAttemptStatus.Prepared);
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
                        Add(insertAttempt, "@ModelId", GuidText(modelId));
                        Add(insertAttempt, "@ProspectiveModelRevisionId", GuidText(attempt.ProspectiveModelRevisionId));
                        Add(insertAttempt, "@Now", DateText(DateTime.UtcNow));
                        insertAttempt.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return Task.FromResult(attempt);
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The v2 rectangle attempt is already reserved.", exception);
            }
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
                using (var binding = connection.CreateCommand())
                {
                    binding.Transaction = transaction;
                    binding.CommandText = @"UPDATE EntityReferenceBindings SET Status = 'Uncertain', UpdatedUtc = @Now
WHERE Status IN ('Pending', 'Active') AND EXISTS (
 SELECT 1 FROM V2MutationAttempts a WHERE a.Id = @Id AND a.Status = 'Prepared'
   AND a.ModelId = EntityReferenceBindings.ModelId AND a.OutputEntityId = EntityReferenceBindings.EntityId
   AND EntityReferenceBindings.ConfigurationKey = (SELECT m.ConfigurationKey FROM ManagedModels m WHERE m.ModelId = a.ModelId));";
                    Add(binding, "@Now", now);
                    Add(binding, "@Id", GuidText(attemptId));
                    binding.ExecuteNonQuery();
                }
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
