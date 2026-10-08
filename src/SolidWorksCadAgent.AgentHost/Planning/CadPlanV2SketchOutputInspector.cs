using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>
    /// Read-only reference-state observation for a normalized v2 Extrude input.
    /// A stored token is not proof of a committed Host mutation or permission to dispatch.
    /// </summary>
    public static class CadPlanV2SketchOutputInspector
    {
        public static async Task<CadPlanV2SketchOutputObservation> ObserveExtrudeInputAsync(
            string persistedPlanJson, string stepKey, Guid managedModelId,
            IModelReferenceStore store, CancellationToken cancellationToken)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (managedModelId == Guid.Empty) throw new ArgumentException("A managed model ID is required.", nameof(managedModelId));
            if (string.IsNullOrWhiteSpace(stepKey)) throw new ArgumentException("A version-2 step key is required.", nameof(stepKey));

            var parsed = CadPlanDocumentReader.ReadPersisted(persistedPlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                throw new ArgumentException("A valid Host-normalized version-2 revision is required: " +
                    string.Join(" ", parsed.Errors), nameof(persistedPlanJson));
            var step = parsed.CandidateV2.Steps.SingleOrDefault(item =>
                string.Equals(item.StepKey, stepKey, StringComparison.Ordinal));
            if (step == null || step.Command != CadCommandNames.Extrude ||
                step.Inputs?.ProfileSketch?.EntityId.HasValue != true)
                throw new ArgumentException("The selected step is not a normalized version-2 Extrude operation.", nameof(stepKey));
            var entityId = step.Inputs.ProfileSketch.EntityId.Value;

            var model = await store.GetModelAsync(managedModelId, cancellationToken).ConfigureAwait(false);
            CadEntityReferenceBinding binding = null;
            if (model != null && !string.IsNullOrWhiteSpace(model.ConfigurationKey))
                binding = await store.GetEntityBindingAsync(
                    managedModelId, entityId, model.ConfigurationKey, cancellationToken).ConfigureAwait(false);

            return new CadPlanV2SketchOutputObservation(
                entityId,
                model?.Status,
                model?.CurrentModelRevisionId,
                binding?.Status,
                binding?.CreatedAtModelRevisionId,
                binding?.NativeReferenceBytes != null && binding.NativeReferenceBytes.Length > 0 &&
                binding.NativeReferenceBytes.Length <= 1024 * 1024);
        }
    }

    public sealed class CadPlanV2SketchOutputObservation
    {
        internal CadPlanV2SketchOutputObservation(
            Guid entityId, CadModelIdentityStatus? modelStatus, Guid? modelRevisionId,
            CadEntityReferenceStatus? referenceStatus, Guid? bindingCreatedAtRevisionId,
            bool hasStoredToken)
        {
            EntityId = entityId;
            ModelStatus = modelStatus;
            ModelRevisionId = modelRevisionId;
            ReferenceStatus = referenceStatus;
            BindingCreatedAtRevisionId = bindingCreatedAtRevisionId;
            HasStoredToken = hasStoredToken;
        }

        public Guid EntityId { get; }
        public CadModelIdentityStatus? ModelStatus { get; }
        public Guid? ModelRevisionId { get; }
        public CadEntityReferenceStatus? ReferenceStatus { get; }
        public Guid? BindingCreatedAtRevisionId { get; }
        public bool HasStoredToken { get; }
    }
}
