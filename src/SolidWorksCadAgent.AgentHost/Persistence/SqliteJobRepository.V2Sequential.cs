using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// <summary>Reserves the next persisted mutation without advancing its model revision.</summary>
        public Task<CadV2MutationAttemptRecord> PrepareV2SequentialAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid modelId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    var context = ReadV2SequentialContext(connection, transaction, jobId, revisionId, stepKey, modelId);
                    if (context.Attempt != null)
                        throw new InvalidOperationException("This v2 step already has a durable attempt; replay is blocked.");
                    var attempt = new CadV2MutationAttemptRecord(Guid.NewGuid(), jobId, revisionId,
                        context.PlanHash, stepKey, modelId, context.Step.OutputEntityId, Guid.NewGuid(),
                        CadV2MutationAttemptStatus.Prepared);
                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT INTO V2MutationAttempts
(Id, JobId, RevisionId, PlanSha256, StepKey, ModelId, OutputEntityId,
 ProspectiveModelRevisionId, Status, CreatedUtc, UpdatedUtc)
VALUES (@Id, @JobId, @RevisionId, @Hash, @StepKey, @ModelId, @EntityId,
 @NextRevision, 'Prepared', @Now, @Now);";
                        Add(insert, "@Id", GuidText(attempt.Id));
                        Add(insert, "@JobId", GuidText(jobId));
                        Add(insert, "@RevisionId", GuidText(revisionId));
                        Add(insert, "@Hash", context.PlanHash);
                        Add(insert, "@StepKey", stepKey);
                        Add(insert, "@ModelId", GuidText(modelId));
                        Add(insert, "@EntityId", attempt.OutputEntityId.HasValue ? (object)GuidText(attempt.OutputEntityId.Value) : DBNull.Value);
                        Add(insert, "@NextRevision", GuidText(attempt.ProspectiveModelRevisionId));
                        Add(insert, "@Now", DateText(DateTime.UtcNow));
                        insert.ExecuteNonQuery();
                    }
                    if (attempt.OutputEntityId.HasValue)
                    {
                        using (var binding = connection.CreateCommand())
                        {
                            binding.Transaction = transaction;
                            binding.CommandText = @"INSERT INTO EntityReferenceBindings
(ModelId, EntityId, EntityKind, ConfigurationKey, NativeObjectKind, ReferenceFormatVersion,
 NativeReferenceBytes, CreatedAtModelRevisionId, LastResolvedModelRevisionId, SemanticFingerprintJson,
 Status, CreatedUtc, UpdatedUtc)
VALUES (@ModelId, @EntityId, 'Sketch', @Configuration, 'SketchFeature', 3,
 NULL, @NextRevision, NULL, NULL, 'Pending', @Now, @Now);";
                            Add(binding, "@ModelId", GuidText(modelId));
                            Add(binding, "@EntityId", GuidText(attempt.OutputEntityId.Value));
                            Add(binding, "@Configuration", context.Model.ConfigurationKey);
                            Add(binding, "@NextRevision", GuidText(attempt.ProspectiveModelRevisionId));
                            Add(binding, "@Now", DateText(DateTime.UtcNow));
                            binding.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                    return Task.FromResult(attempt);
                }
            }
            catch (SQLiteException exception) when (exception.ResultCode == SQLiteErrorCode.Constraint)
            {
                throw new InvalidOperationException("The v2 attempt or sketch output is already reserved or violates ownership constraints.", exception);
            }
        }

        /// <summary>Rechecks durable provenance immediately before dispatch; this never authorizes replay.</summary>
        public Task<CadV2MutationAttemptRecord> ValidateV2SequentialAttemptAsync(
            Guid jobId, Guid revisionId, string stepKey, Guid attemptId, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            GuidText(attemptId);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var reserved = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey);
                if (reserved == null || reserved.Id != attemptId)
                    throw new InvalidOperationException("The dispatch does not match its exact reserved v2 attempt.");
                var context = ReadV2SequentialContext(connection, transaction, jobId, revisionId, stepKey, reserved.ModelId);
                RequireV2SequentialPrepared(context);
                if (context.Step.Command == CadCommandNames.CreateSketch)
                    RequireV2SequentialBinding(connection, transaction, context.Model, context.Attempt, false);
                transaction.Commit();
                return Task.FromResult(context.Attempt);
            }
        }

        public Task CommitV2SequentialOutcomeAsync(Guid jobId, Guid revisionId, string stepKey,
            Guid modelId, CadCommandResult result, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var context = ReadV2SequentialContext(connection, transaction, jobId, revisionId, stepKey, modelId);
                RequireV2SequentialPrepared(context);
                if (context.Step.Command == CadCommandNames.SavePart ||
                    (context.Step.Command == CadCommandNames.AddRectangle && context.Step.OperationVersion == 2) ||
                    (context.Step.Command == CadCommandNames.Extrude && context.Step.OperationVersion == 3) || !MatchesV2SequentialOutcome(context.Step, result))
                    throw new InvalidOperationException("The observed result does not match the exact persisted v2 mutation contract.");
                if (context.Step.Command == CadCommandNames.CreateSketch)
                    RequireV2SequentialBinding(connection, transaction, context.Model, context.Attempt, true);
                ApplyV2SequentialOutcome(connection, transaction, context, null, null);
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task CommitV2SequentialSaveOutcomeAsync(Guid jobId, Guid revisionId, string stepKey,
            Guid modelId, string canonicalPath, string sha256, CadCommandResult result, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            if (result == null || !result.Success || result.Error != null || result.Data == null ||
                string.IsNullOrWhiteSpace(canonicalPath) || !Path.IsPathRooted(canonicalPath) ||
                Path.GetFullPath(canonicalPath) != canonicalPath || !IsSha256(sha256) ||
                result.Data["path"]?.Type != JTokenType.String ||
                !string.Equals(result.Data.Value<string>("path"), canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                result.Data["errors"]?.Type != JTokenType.Integer || result.Data.Value<int>("errors") != 0)
                throw new InvalidOperationException("The saved path, hash or native save evidence is invalid.");
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var context = ReadV2SequentialContext(connection, transaction, jobId, revisionId, stepKey, modelId);
                RequireV2SequentialPrepared(context);
                if (context.Step.Command != CadCommandNames.SavePart)
                    throw new InvalidOperationException("Only the terminal persisted SavePart can commit a saved model.");
                ApplyV2SequentialOutcome(connection, transaction, context, canonicalPath, sha256);
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        private sealed class V2SequentialContext
        {
            internal CadPlanV2Step Step;
            internal CadModelIdentityRecord Model;
            internal CadV2MutationAttemptRecord Attempt;
            internal string PlanHash;
            internal CadPlanV2Document Plan;
        }

        private static V2SequentialContext ReadV2SequentialContext(SQLiteConnection connection,
            SQLiteTransaction transaction, Guid jobId, Guid revisionId, string stepKey, Guid modelId)
        {
            GuidText(jobId); GuidText(revisionId); GuidText(modelId);
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A step key is required.", nameof(stepKey));
            string json;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = @"SELECT r.PlanJson FROM Revisions r JOIN Jobs j ON j.Id = r.JobId
WHERE r.JobId = @JobId AND r.Id = @RevisionId AND j.IsSimulated = 0
AND j.PlanValidated = 1 AND j.State = @Executing
AND r.Id = (SELECT Id FROM Revisions WHERE JobId = @JobId ORDER BY RevisionNumber DESC LIMIT 1);";
                Add(current, "@JobId", GuidText(jobId));
                Add(current, "@RevisionId", GuidText(revisionId));
                Add(current, "@Executing", (int)JobState.Executing);
                json = current.ExecuteScalar() as string;
            }
            var parsed = CadPlanDocumentReader.ReadPersisted(json);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                throw new InvalidOperationException("A current normalized v2 plan on a real validated executing job is required.");
            var steps = parsed.CandidateV2.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var feature = step.Command == CadCommandNames.Extrude || step.Command == CadCommandNames.CutExtrude;
                var allowed = i == 0 ? step.Command == CadCommandNames.NewPart :
                    step.Command == CadCommandNames.CreateSketch || IsV2SequentialPrimitive(step.Command) ||
                    step.Command == CadCommandNames.ExitSketch || feature || step.Command == CadCommandNames.SavePart;
                if (!allowed || !CadParametricPlateContract.AllowsOperationVersion(step.Command, step.OperationVersion.Value) ||
                    (step.Command == CadCommandNames.SavePart &&
                     (i != steps.Count - 1 || string.IsNullOrWhiteSpace(step.Parameters.Value<string>("path")) ||
                      step.Parameters.Value<bool?>("allowOverwrite") == true)))
                    throw new InvalidOperationException("The persisted plan contains an unsupported sequential v2 operation or save position.");
            }
            var index = steps.FindIndex(step => step.StepKey == stepKey);
            if (index < 1) throw new InvalidOperationException("The requested step is not a post-NewPart mutation in the stored plan.");
            var hash = HashPlan(json);
            var prior = new Dictionary<string, CadV2MutationAttemptRecord>(StringComparer.Ordinal);
            for (var i = 0; i < index; i++)
            {
                var attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, steps[i].StepKey);
                if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Applied ||
                    attempt.JobId != jobId || attempt.RevisionId != revisionId || attempt.ModelId != modelId ||
                    attempt.PlanSha256 != hash || attempt.OutputEntityId != steps[i].OutputEntityId)
                    throw new InvalidOperationException("Every preceding v2 step must be Applied with the exact plan, model and output identity.");
                prior.Add(steps[i].StepKey, attempt);
            }
            CadModelIdentityRecord model;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = @"SELECT ModelId, ParentModelId, DocumentKind, Status, CustomPropertyKey, CanonicalPath, LastSavedSha256,
CurrentModelRevisionId, ConfigurationKey, SolidWorksRevision, RegistryVersion, CreatedUtc, UpdatedUtc
FROM ManagedModels WHERE ModelId = @ModelId;";
                Add(current, "@ModelId", GuidText(modelId));
                using (var reader = current.ExecuteReader()) model = reader.Read() ? ReadModel(reader) : null;
            }
            if (model == null || model.Status != CadModelIdentityStatus.ActiveUnsaved || model.DocumentKind != "Part" ||
                !string.IsNullOrWhiteSpace(model.CanonicalPath) || !string.IsNullOrWhiteSpace(model.LastSavedSha256) ||
                model.CurrentModelRevisionId != prior[steps[index - 1].StepKey].ProspectiveModelRevisionId)
                throw new InvalidOperationException("The owned unsaved part must be at its immediate predecessor's committed revision.");
            using (var owner = connection.CreateCommand())
            {
                owner.Transaction = transaction;
                owner.CommandText = "SELECT COUNT(*) FROM V2ModelOwnerships WHERE ModelId = @ModelId AND JobId = @JobId AND FirstRevisionId = @RevisionId;";
                Add(owner, "@ModelId", GuidText(modelId));
                Add(owner, "@JobId", GuidText(jobId));
                Add(owner, "@RevisionId", GuidText(revisionId));
                if (Convert.ToInt32(owner.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("The model is not owned by this exact job and first revision.");
            }
            // Validate each persisted producer against its own creation revision, including older sketches.
            foreach (var producer in steps.Take(index).Where(step => step.Command == CadCommandNames.CreateSketch))
                RequireV2SequentialBinding(connection, transaction, model, prior[producer.StepKey], true);
            var target = steps[index];
            if (target.Inputs?.ProfileSketch != null)
            {
                var reference = target.Inputs.ProfileSketch;
                var producer = steps.Take(index).SingleOrDefault(step => step.Command == CadCommandNames.CreateSketch && step.OutputKey == reference.OutputKey);
                if (producer == null || reference.Kind != "Sketch" || reference.EntityId != producer.OutputEntityId)
                    throw new InvalidOperationException("The feature must reference its exact preceding normalized sketch producer.");
            }
            return new V2SequentialContext
            {
                Step = target, Model = model, PlanHash = hash, Plan = parsed.CandidateV2,
                Attempt = FindV2MutationAttemptInTransaction(connection, transaction, jobId, revisionId, stepKey)
            };
        }

        private static void RequireV2SequentialPrepared(V2SequentialContext context)
        {
            var attempt = context.Attempt;
            if (attempt == null || attempt.Status != CadV2MutationAttemptStatus.Prepared ||
                attempt.ModelId != context.Model.ModelId || attempt.PlanSha256 != context.PlanHash ||
                attempt.OutputEntityId != context.Step.OutputEntityId ||
                attempt.ProspectiveModelRevisionId == context.Model.CurrentModelRevisionId)
                throw new InvalidOperationException("The mutation requires its exact Prepared attempt at the pre-dispatch revision.");
        }

        private static void RequireV2SequentialBinding(SQLiteConnection connection, SQLiteTransaction transaction,
            CadModelIdentityRecord model, CadV2MutationAttemptRecord producer, bool active)
        {
            if (!producer.OutputEntityId.HasValue)
                throw new InvalidOperationException("A sketch producer must declare its normalized output identity.");
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT EntityKind, NativeObjectKind, ReferenceFormatVersion,
NativeReferenceBytes, CreatedAtModelRevisionId, Status FROM EntityReferenceBindings
WHERE ModelId = @ModelId AND EntityId = @EntityId AND ConfigurationKey = @Configuration;";
                Add(command, "@ModelId", GuidText(model.ModelId));
                Add(command, "@EntityId", GuidText(producer.OutputEntityId.Value));
                Add(command, "@Configuration", model.ConfigurationKey);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("The exact plan-owned sketch binding is missing.");
                    var bytes = reader.IsDBNull(3) ? null : (byte[])reader[3];
                    if (reader.GetString(0) != "Sketch" || reader.GetString(1) != "SketchFeature" || reader.GetInt32(2) != 3 ||
                        Guid.Parse(reader.GetString(4)) != producer.ProspectiveModelRevisionId ||
                        reader.GetString(5) != (active ? CadEntityReferenceStatus.Active : CadEntityReferenceStatus.Pending).ToString() ||
                        (active ? bytes == null || bytes.Length == 0 || bytes.Length > 1024 * 1024 : bytes != null))
                        throw new InvalidOperationException("The sketch binding does not match its producer, configuration, creation revision and native reference state.");
                }
            }
        }

        private static void ApplyV2SequentialOutcome(SQLiteConnection connection, SQLiteTransaction transaction,
            V2SequentialContext context, string path, string sha256)
        {
            using (var model = connection.CreateCommand())
            {
                model.Transaction = transaction;
                model.CommandText = @"UPDATE ManagedModels SET CurrentModelRevisionId = @NextRevision,
Status = @Status, CanonicalPath = @Path, LastSavedSha256 = @Hash, UpdatedUtc = @Now
WHERE ModelId = @ModelId AND Status = 'ActiveUnsaved' AND CurrentModelRevisionId = @PreviousRevision
AND CanonicalPath IS NULL AND LastSavedSha256 IS NULL;";
                Add(model, "@NextRevision", GuidText(context.Attempt.ProspectiveModelRevisionId));
                Add(model, "@Status", (path == null ? CadModelIdentityStatus.ActiveUnsaved : CadModelIdentityStatus.ActiveSaved).ToString());
                Add(model, "@Path", (object)path ?? DBNull.Value);
                Add(model, "@Hash", (object)sha256 ?? DBNull.Value);
                Add(model, "@Now", DateText(DateTime.UtcNow));
                Add(model, "@ModelId", GuidText(context.Model.ModelId));
                Add(model, "@PreviousRevision", GuidText(context.Model.CurrentModelRevisionId));
                if (model.ExecuteNonQuery() != 1) throw new InvalidOperationException("The model revision changed before the v2 outcome commit.");
            }
            using (var attempt = connection.CreateCommand())
            {
                attempt.Transaction = transaction;
                attempt.CommandText = "UPDATE V2MutationAttempts SET Status = 'Applied', UpdatedUtc = @Now WHERE Id = @Id AND Status = 'Prepared';";
                Add(attempt, "@Now", DateText(DateTime.UtcNow));
                Add(attempt, "@Id", GuidText(context.Attempt.Id));
                if (attempt.ExecuteNonQuery() != 1) throw new InvalidOperationException("The v2 mutation attempt is no longer Prepared.");
            }
        }

        private static bool IsV2SequentialPrimitive(string command) => command == CadCommandNames.AddRectangle ||
            command == CadCommandNames.AddCircle || command == CadCommandNames.AddLine || command == CadCommandNames.AddArc ||
            command == CadCommandNames.AddSlot || command == CadCommandNames.AddRegularPolygon;

        private static bool MatchesV2SequentialOutcome(CadPlanV2Step step, CadCommandResult result)
        {
            if (result == null || !result.Success || result.Error != null || result.Data == null) return false;
            var data = result.Data;
            if (step.Command == CadCommandNames.CreateSketch)
                return data["entityId"]?.Type == JTokenType.String && Guid.TryParse(data.Value<string>("entityId"), out var entity) && entity == step.OutputEntityId;
            if (step.Command == CadCommandNames.ExitSketch)
                return data["exited"]?.Type == JTokenType.Boolean && data.Value<bool>("exited");
            if (step.Command == CadCommandNames.Extrude || step.Command == CadCommandNames.CutExtrude)
            {
                if (data["featureName"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(data.Value<string>("featureName"))) return false;
                if (!MatchesV2SequentialEcho(step.Parameters, data)) return false;
                return step.Command != CadCommandNames.CutExtrude || step.Parameters.Value<string>("endCondition") != "ThroughAll" ||
                    data["depthMm"] == null || data["depthMm"].Type == JTokenType.Null;
            }
            if (!IsV2SequentialPrimitive(step.Command)) return false;
            if (step.Command == CadCommandNames.AddRectangle || step.Command == CadCommandNames.AddCircle)
                return MatchesV2SequentialEcho(step.Parameters, data);
            if (data["created"]?.Type != JTokenType.Boolean || !data.Value<bool>("created") ||
                !MatchesV2SequentialEcho(step.Parameters, data["geometry"] as JObject)) return false;
            if (step.Command == CadCommandNames.AddSlot || step.Command == CadCommandNames.AddRegularPolygon)
            {
                var count = step.Command == CadCommandNames.AddSlot ? PrismaticProfileGeometry.Slot(step.Parameters).Count :
                    PrismaticProfileGeometry.RegularPolygon(step.Parameters).Count;
                return data["segmentCount"]?.Type == JTokenType.Integer && data.Value<int>("segmentCount") == count;
            }
            return true;
        }

        private static bool MatchesV2SequentialEcho(JObject expected, JObject observed)
        {
            if (expected == null || observed == null) return false;
            foreach (var property in expected.Properties())
            {
                var value = observed[property.Name];
                if (value == null) return false;
                if (property.Value.Type == JTokenType.Integer || property.Value.Type == JTokenType.Float)
                {
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return false;
                    var number = value.Value<double>();
                    if (double.IsNaN(number) || double.IsInfinity(number) || number != property.Value.Value<double>()) return false;
                }
                else if (!JToken.DeepEquals(property.Value, value)) return false;
            }
            return true;
        }
    }
}
