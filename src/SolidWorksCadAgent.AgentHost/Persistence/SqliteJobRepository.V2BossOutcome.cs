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
        /// <summary>Atomically records the first boss only after its observed result matches the stored plan.</summary>
        public Task CommitV2BossOutcomeAsync(Guid jobId, Guid revisionId, string stepKey,
            Guid modelId, CadCommandResult observedResult, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(jobId); GuidText(revisionId); GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            if (observedResult == null || !observedResult.Success || observedResult.Error != null || observedResult.Data == null)
                throw new InvalidOperationException("Only an observed successful boss result can be committed.");

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
                if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null || parsed.CandidateV2.Steps.Count < 5)
                    throw new InvalidOperationException("The Boss result does not belong to a current normalized v2 plan.");
                var steps = parsed.CandidateV2.Steps;
                var sketch = steps[1]; var rectangle = steps[2]; var exit = steps[3]; var boss = steps[4];
                if (steps[0].Command != CadCommandNames.NewPart ||
                    sketch.Command != CadCommandNames.CreateSketch || sketch.OperationVersion != 1 || !sketch.OutputEntityId.HasValue ||
                    rectangle.Command != CadCommandNames.AddRectangle || rectangle.OperationVersion != 1 || rectangle.OutputEntityId.HasValue ||
                    exit.Command != CadCommandNames.ExitSketch || exit.OperationVersion != 1 || exit.OutputEntityId.HasValue ||
                    boss.StepKey != stepKey || boss.Command != CadCommandNames.Extrude || boss.OperationVersion != 2 ||
                    boss.OutputEntityId.HasValue || boss.Parameters == null ||
                    boss.Inputs?.ProfileSketch?.EntityId != sketch.OutputEntityId ||
                    boss.Inputs.ProfileSketch.Kind != "Sketch" || boss.Inputs.ProfileSketch.OutputKey != sketch.OutputKey ||
                    !MatchesBossResult(boss.Parameters, observedResult.Data))
                    throw new InvalidOperationException("The boss result does not match the exact fifth step and observed feature contract.");

                var hash = HashPlan(planJson);
                var newPartAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, steps[0].StepKey);
                var sketchAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, sketch.StepKey);
                var rectangleAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, rectangle.StepKey);
                var exitAttempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, exit.StepKey);
                var attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey);
                if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Prepared || attempt.ModelId != modelId ||
                    attempt.OutputEntityId.HasValue || attempt.PlanSha256 != hash ||
                    newPartAttempt == null || newPartAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    newPartAttempt.ModelId != modelId || newPartAttempt.OutputEntityId.HasValue || newPartAttempt.PlanSha256 != hash ||
                    sketchAttempt == null || sketchAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    sketchAttempt.ModelId != modelId || sketchAttempt.OutputEntityId != sketch.OutputEntityId || sketchAttempt.PlanSha256 != hash ||
                    rectangleAttempt == null || rectangleAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    rectangleAttempt.ModelId != modelId || rectangleAttempt.OutputEntityId.HasValue || rectangleAttempt.PlanSha256 != hash ||
                    exitAttempt == null || exitAttempt.Status != CadV2MutationAttemptStatus.Applied ||
                    exitAttempt.ModelId != modelId || exitAttempt.OutputEntityId.HasValue || exitAttempt.PlanSha256 != hash)
                    throw new InvalidOperationException("The Boss result requires its exact Prepared attempt and Applied predecessors.");

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
                    model.CurrentModelRevisionId != exitAttempt.ProspectiveModelRevisionId)
                    throw new InvalidOperationException("The owned unsaved part is not at the committed ExitSketch revision.");
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
                    Add(updateModel, "@PreviousRevision", GuidText(exitAttempt.ProspectiveModelRevisionId));
                    if (updateModel.ExecuteNonQuery() != 1) throw new InvalidOperationException("The model revision changed before boss commit.");
                }
                using (var updateAttempt = connection.CreateCommand())
                {
                    updateAttempt.Transaction = transaction;
                    updateAttempt.CommandText = "UPDATE V2MutationAttempts SET Status = 'Applied', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                    Add(updateAttempt, "@Now", DateText(DateTime.UtcNow)); Add(updateAttempt, "@Id", GuidText(attempt.Id));
                    if (updateAttempt.ExecuteNonQuery() != 1) throw new InvalidOperationException("The boss attempt is no longer Prepared.");
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        private static bool MatchesBossResult(JObject expected, JObject observed)
        {
            var planned = expected?["depthMm"];
            var returned = observed?["depthMm"];
            return planned != null && returned != null &&
                (planned.Type == JTokenType.Integer || planned.Type == JTokenType.Float) &&
                (returned.Type == JTokenType.Integer || returned.Type == JTokenType.Float) &&
                planned.Value<double>() == returned.Value<double>() &&
                observed["featureName"]?.Type == JTokenType.String &&
                !string.IsNullOrWhiteSpace(observed.Value<string>("featureName"));
        }

    }
}
