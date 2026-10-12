using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
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
        /// <summary>Atomically records only an observed closure of the fourth, Host-bound sketch step.</summary>
        public Task CommitV2ExitSketchOutcomeAsync(Guid jobId, Guid revisionId, string stepKey,
            Guid modelId, CadCommandResult observedResult, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId); GuidText(revisionId); GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            if (observedResult == null || !observedResult.Success || observedResult.Error != null ||
                observedResult.Data?["exited"]?.Type != JTokenType.Boolean || !observedResult.Data.Value<bool>("exited"))
                throw new InvalidOperationException("Only an observed sketch closure can be committed.");

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                string planJson;
                using (var current = connection.CreateCommand())
                {
                    current.Transaction = transaction;
                    current.CommandText = @"SELECT r.PlanJson FROM Revisions r JOIN Jobs j ON j.Id = r.JobId
WHERE r.JobId = @JobId AND r.Id = @RevisionId AND j.IsSimulated = 0 AND j.PlanValidated = 1
AND j.State = @Executing AND r.Id = (SELECT Id FROM Revisions WHERE JobId = @JobId ORDER BY RevisionNumber DESC LIMIT 1);";
                    Add(current, "@JobId", GuidText(jobId));
                    Add(current, "@RevisionId", GuidText(revisionId));
                    Add(current, "@Executing", (int)JobState.Executing);
                    planJson = current.ExecuteScalar() as string;
                }
                var parsed = CadPlanDocumentReader.ReadPersisted(planJson);
                if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null || parsed.CandidateV2.Steps.Count < 4)
                    throw new InvalidOperationException("The ExitSketch result does not belong to a current normalized v2 plan.");
                var steps = parsed.CandidateV2.Steps;
                var sketch = steps[1]; var rectangle = steps[2]; var exit = steps[3];
                if (steps[0].Command != CadCommandNames.NewPart ||
                    sketch.Command != CadCommandNames.CreateSketch || sketch.OperationVersion != 1 || !sketch.OutputEntityId.HasValue ||
                    rectangle.Command != CadCommandNames.AddRectangle || rectangle.OperationVersion != 1 || rectangle.OutputEntityId.HasValue ||
                    exit.StepKey != stepKey || exit.Command != CadCommandNames.ExitSketch || exit.OperationVersion != 1 ||
                    exit.OutputEntityId.HasValue || exit.Parameters == null || exit.Parameters.HasValues)
                    throw new InvalidOperationException("The ExitSketch result is not the exact fourth no-output step.");

                var hash = HashPlan(planJson);
                var newPartAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, steps[0].StepKey);
                var sketchAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, sketch.StepKey);
                var rectangleAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, rectangle.StepKey);
                var attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey);
                if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Prepared || attempt.ModelId != modelId ||
                    attempt.OutputEntityId.HasValue || attempt.PlanSha256 != hash ||
                    newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue || newPartAttempt.PlanSha256 != hash ||
                    sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    sketchAttempt.ModelId != modelId || sketchAttempt.OutputEntityId != sketch.OutputEntityId || sketchAttempt.PlanSha256 != hash ||
                    rectangleAttempt == null || rectangleAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    rectangleAttempt.ModelId != modelId || rectangleAttempt.OutputEntityId.HasValue || rectangleAttempt.PlanSha256 != hash)
                    throw new InvalidOperationException("The ExitSketch result requires its exact Prepared attempt and Applied predecessors.");

                CadModelIdentityRecord model;
                using (var currentModel = connection.CreateCommand())
                {
                    currentModel.Transaction = transaction;
                    currentModel.CommandText = @"SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc FROM ManagedModels WHERE ModelId = @ModelId;";
                    Add(currentModel, "@ModelId", GuidText(modelId));
                    using (var reader = currentModel.ExecuteReader()) model = reader.Read() ? ReadModel(reader) : null;
                }
                if (model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved || model.DocumentKind != "Part" ||
                    !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256) ||
                    model.CurrentModelRevisionId != rectangleAttempt.ProspectiveModelRevisionId)
                    throw new InvalidOperationException("The owned unsaved part is not at the committed rectangle revision.");
                using (var owner = connection.CreateCommand())
                {
                    owner.Transaction = transaction;
                    owner.CommandText = @"SELECT COUNT(*) FROM V2ModelOwnerships
WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                    Add(owner, "@ModelId", GuidText(modelId)); Add(owner, "@JobId", GuidText(jobId));
                    Add(owner, "@RevisionId", GuidText(revisionId));
                    if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("The part is not owned by this job and revision.");
                }
                using (var binding = connection.CreateCommand())
                {
                    binding.Transaction = transaction;
                    binding.CommandText = @"SELECT EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, Status FROM EntityReferenceBindings
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                    Add(binding, "@ModelId", GuidText(modelId)); Add(binding, "@EntityId", GuidText(sketch.OutputEntityId.Value));
                    Add(binding, "@ConfigurationKey", model.ConfigurationKey);
                    using (var reader = binding.ExecuteReader())
                    {
                        var bytes = reader.Read() && !reader.IsDBNull(4) ? (byte[])reader[4] : null;
                        if (bytes == null || bytes.Length == 0 || bytes.Length > 1024 * 1024 ||
                            reader.GetString(0) != "Sketch" || reader.GetString(1) != model.ConfigurationKey ||
                            reader.GetString(2) != "SketchFeature" || reader.GetInt32(3) != 3 ||
                            Guid.Parse(reader.GetString(5)) != sketchAttempt.ProspectiveModelRevisionId ||
                            reader.GetString(6) != CadEntityReferenceStatus.Active.ToString())
                            throw new InvalidOperationException("The plan-owned sketch reference is unavailable.");
                    }
                }
                using (var updateModel = connection.CreateCommand())
                {
                    updateModel.Transaction = transaction;
                    updateModel.CommandText = @"UPDATE ManagedModels SET CurrentModelRevisionId = @NextRevision, UpdatedUtc = @Now
WHERE ModelId = @ModelId AND Status = @Active AND CurrentModelRevisionId = @PreviousRevision;";
                    Add(updateModel, "@NextRevision", GuidText(attempt.ProspectiveModelRevisionId));
                    Add(updateModel, "@Now", DateText(DateTime.UtcNow)); Add(updateModel, "@ModelId", GuidText(modelId));
                    Add(updateModel, "@Active", CadModelIdentityStatus.ActiveUnsaved.ToString());
                    Add(updateModel, "@PreviousRevision", GuidText(rectangleAttempt.ProspectiveModelRevisionId));
                    if (updateModel.ExecuteNonQuery() != 1) throw new InvalidOperationException("The model revision changed before closure could commit.");
                }
                using (var updateAttempt = connection.CreateCommand())
                {
                    updateAttempt.Transaction = transaction;
                    updateAttempt.CommandText = "UPDATE V2MutationAttempts SET Status = 'Applied', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                    Add(updateAttempt, "@Now", DateText(DateTime.UtcNow)); Add(updateAttempt, "@Id", GuidText(attempt.Id));
                    if (updateAttempt.ExecuteNonQuery() != 1) throw new InvalidOperationException("The closure attempt is no longer Prepared.");
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reserves only the fourth, no-output ExitSketch step after the first committed
        /// rectangle. This records intent; neither native sketch state nor model revision
        /// changes until a later correlated dispatch is committed.
        /// </summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2ExitSketchAttemptAsync(
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
                        parsed.CandidateV2.Steps.Count < 4)
                        throw new InvalidOperationException("A current normalized v2 plan with a fourth ExitSketch step is required.");
                    var steps = parsed.CandidateV2.Steps;
                    var newPart = steps[0];
                    var sketch = steps[1];
                    var rectangle = steps[2];
                    var exit = steps[3];
                    if (newPart.Command != CadCommandNames.NewPart ||
                        sketch.Command != CadCommandNames.CreateSketch || sketch.OperationVersion != 1 || !sketch.OutputEntityId.HasValue ||
                        rectangle.Command != CadCommandNames.AddRectangle || rectangle.OperationVersion != 1 || rectangle.OutputEntityId.HasValue ||
                        exit.StepKey != stepKey || exit.Command != CadCommandNames.ExitSketch || exit.OperationVersion != 1 ||
                        exit.OutputEntityId.HasValue || exit.Parameters == null || exit.Parameters.HasValues)
                        throw new InvalidOperationException("Only the exact fourth normalized ExitSketch after a rectangle can be prepared.");

                    var planHash = HashPlan(planJson);
                    var newPartAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, newPart.StepKey);
                    var sketchAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, sketch.StepKey);
                    var rectangleAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, rectangle.StepKey);
                    if (newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue ||
                        sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        sketchAttempt.ModelId != modelId || sketchAttempt.OutputEntityId != sketch.OutputEntityId ||
                        rectangleAttempt == null || rectangleAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        rectangleAttempt.ModelId != modelId || rectangleAttempt.OutputEntityId.HasValue ||
                        newPartAttempt.PlanSha256 != planHash || sketchAttempt.PlanSha256 != planHash ||
                        rectangleAttempt.PlanSha256 != planHash)
                        throw new InvalidOperationException("Matching Applied NewPart, CreateSketch and AddRectangle attempts are required.");

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
                        model.CurrentModelRevisionId != rectangleAttempt.ProspectiveModelRevisionId ||
                        model.DocumentKind != "Part" || !string.IsNullOrWhiteSpace(model.CanonicalPath) ||
                        !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                        throw new InvalidOperationException("The owned unsaved part is not at the committed rectangle revision.");

                    using (var owner = connection.CreateCommand())
                    {
                        owner.Transaction = transaction;
                        owner.CommandText = @"SELECT COUNT(*) FROM V2ModelOwnerships
WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                        Add(owner, "@ModelId", GuidText(modelId));
                        Add(owner, "@JobId", GuidText(jobId));
                        Add(owner, "@RevisionId", GuidText(revisionId));
                        if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                            throw new InvalidOperationException("The model is not owned by this job and revision.");
                    }

                    using (var binding = connection.CreateCommand())
                    {
                        binding.Transaction = transaction;
                        binding.CommandText = @"SELECT EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, Status FROM EntityReferenceBindings
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @ConfigurationKey;";
                        Add(binding, "@ModelId", GuidText(modelId));
                        Add(binding, "@EntityId", GuidText(sketch.OutputEntityId.Value));
                        Add(binding, "@ConfigurationKey", model.ConfigurationKey);
                        using (var reader = binding.ExecuteReader())
                        {
                            var bytes = reader.Read() && !reader.IsDBNull(4) ? (byte[])reader[4] : null;
                            if (bytes == null || bytes.Length == 0 || bytes.Length > 1024 * 1024 ||
                                reader.GetString(0) != "Sketch" || reader.GetString(1) != model.ConfigurationKey ||
                                reader.GetString(2) != "SketchFeature" || reader.GetInt32(3) != 3 ||
                                Guid.Parse(reader.GetString(5)) != sketchAttempt.ProspectiveModelRevisionId ||
                                reader.GetString(6) != CadEntityReferenceStatus.Active.ToString())
                                throw new InvalidOperationException("The exact preceding sketch has no active, bounded native reference.");
                        }
                    }

                    var attempt = new CadV2MutationAttemptRecord(Guid.NewGuid(), jobId, revisionId,
                        planHash, stepKey, modelId, null, Guid.NewGuid(), CadV2MutationAttemptStatus.Prepared);
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"
INSERT INTO V2MutationAttempts
(Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId,
 ProspectiveModelRevisionId, Status, CreatedUtc, UpdatedUtc)
VALUES (@Id, @JobId, @RevisionId, @PlanSha256, @StepKey, @ModelId, NULL,
 @ProspectiveModelRevisionId, 'Prepared', @Now, @Now);";
                        Add(insert, "@Id", GuidText(attempt.Id));
                        Add(insert, "@JobId", GuidText(jobId));
                        Add(insert, "@RevisionId", GuidText(revisionId));
                        Add(insert, "@PlanSha256", planHash);
                        Add(insert, "@StepKey", stepKey);
                        Add(insert, "@ModelId", GuidText(modelId));
                        Add(insert, "@ProspectiveModelRevisionId", GuidText(attempt.ProspectiveModelRevisionId));
                        Add(insert, "@Now", DateText(DateTime.UtcNow));
                        insert.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    return Task.FromResult(attempt);
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The v2 ExitSketch attempt is already reserved.", exception);
            }
        }
    }
}
