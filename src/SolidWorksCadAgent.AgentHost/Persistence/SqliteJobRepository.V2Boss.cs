using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository
    {
        /// <summary>Reserves the first boss as a no-output attempt under the current v2 sketch-only identity schema.</summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2BossAttemptAsync(
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
                        parsed.CandidateV2.Steps.Count < 5)
                        throw new InvalidOperationException("A current normalized v2 plan with a fifth boss step is required.");
                    var steps = parsed.CandidateV2.Steps;
                    var newPart = steps[0];
                    var sketch = steps[1];
                    var rectangle = steps[2];
                    var exit = steps[3];
                    var boss = steps[4];
                    if (newPart.Command != CadCommandNames.NewPart ||
                        sketch.Command != CadCommandNames.CreateSketch || sketch.OperationVersion != 1 || !sketch.OutputEntityId.HasValue ||
                        rectangle.Command != CadCommandNames.AddRectangle || rectangle.OperationVersion != 1 || rectangle.OutputEntityId.HasValue ||
                        exit.Command != CadCommandNames.ExitSketch || exit.OperationVersion != 1 || exit.OutputEntityId.HasValue ||
                        boss.StepKey != stepKey || boss.Command != CadCommandNames.Extrude || boss.OperationVersion != 2 ||
                        boss.OutputEntityId.HasValue || boss.Parameters == null ||
                        boss.Inputs?.ProfileSketch?.EntityId != sketch.OutputEntityId ||
                        boss.Inputs.ProfileSketch.Kind != "Sketch" ||
                        boss.Inputs.ProfileSketch.OutputKey != sketch.OutputKey)
                        throw new InvalidOperationException("Only the exact fifth normalized boss bound to the preceding sketch can be prepared.");

                    var planHash = HashPlan(planJson);
                    var newPartAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, newPart.StepKey);
                    var sketchAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, sketch.StepKey);
                    var rectangleAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, rectangle.StepKey);
                    var exitAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, exit.StepKey);
                    if (newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue ||
                        sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        sketchAttempt.ModelId != modelId || sketchAttempt.OutputEntityId != sketch.OutputEntityId ||
                        rectangleAttempt == null || rectangleAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        rectangleAttempt.ModelId != modelId || rectangleAttempt.OutputEntityId.HasValue ||
                        exitAttempt == null || exitAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                        exitAttempt.ModelId != modelId || exitAttempt.OutputEntityId.HasValue ||
                        newPartAttempt.PlanSha256 != planHash || sketchAttempt.PlanSha256 != planHash ||
                        rectangleAttempt.PlanSha256 != planHash || exitAttempt.PlanSha256 != planHash)
                        throw new InvalidOperationException("Matching Applied NewPart, CreateSketch, AddRectangle and ExitSketch attempts are required.");

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
                        model.CurrentModelRevisionId != exitAttempt.ProspectiveModelRevisionId ||
                        model.DocumentKind != "Part" || !string.IsNullOrWhiteSpace(model.CanonicalPath) ||
                        !string.IsNullOrWhiteSpace(model.LastSavedSha256))
                        throw new InvalidOperationException("The owned unsaved part is not at the committed ExitSketch revision.");

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
                throw new InvalidOperationException("The v2 Boss attempt is already reserved.", exception);
            }
        }
    }
}
