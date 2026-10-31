using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

[assembly: InternalsVisibleTo("SolidWorksCadAgent.IntegrationTests")]

namespace SolidWorksCadAgent.SolidWorksBridge.References
{
    // Native token bytes never appear in command results or plan JSON.
    internal sealed class SolidWorksReferenceResolver
    {
        private readonly IModelReferenceStore _store;

        internal SolidWorksReferenceResolver(IModelReferenceStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        internal async Task<SketchReferencePreparation> PrepareSketchCaptureAsync(Guid? modelId, Guid? entityId, CancellationToken token)
        {
            if (!modelId.HasValue || modelId.Value == Guid.Empty || !entityId.HasValue || entityId.Value == Guid.Empty)
                return SketchReferencePreparation.Fail(Error("INVALID_IDENTITY_CONTEXT", "Validate", "Managed sketch creation requires model and entity IDs."));

            var model = await _store.GetModelAsync(modelId.Value, token).ConfigureAwait(false);
            if (model == null)
                return SketchReferencePreparation.Fail(Error("MODEL_REFERENCE_NOT_FOUND", "Validate", "The managed model ID is not registered."));
            if (model.Status != CadModelIdentityStatus.ActiveUnsaved && model.Status != CadModelIdentityStatus.ActiveSaved)
                return SketchReferencePreparation.Fail(Error("MODEL_REFERENCE_UNAVAILABLE", "Validate", "The managed model is not active."));
            if (string.IsNullOrWhiteSpace(model.ConfigurationKey))
                return SketchReferencePreparation.Fail(Error("SKETCH_REFERENCE_CONFIGURATION_MISMATCH", "Validate", "The managed model has no configuration binding."));

            var binding = await _store.GetEntityBindingAsync(modelId.Value, entityId.Value, model.ConfigurationKey, token).ConfigureAwait(false);
            if (binding == null)
                return SketchReferencePreparation.Fail(Error("SKETCH_REFERENCE_NOT_FOUND", "Validate", "The sketch entity ID is not registered for this model and configuration."));
            var error = ValidateSketchBinding(model, binding, entityId.Value, true);
            return error == null ? new SketchReferencePreparation(model, binding) : SketchReferencePreparation.Fail(error);
        }

        internal async Task<CadCommandResult> PersistSketchCaptureAsync(SketchReferencePreparation prepared, byte[] tokenBytes, CancellationToken token)
        {
            if (prepared?.Binding == null || prepared.Error != null)
                return Failure(prepared?.Error ?? Error("SKETCH_REFERENCE_NOT_FOUND", "Validate", "No pending sketch binding is available."));
            if (tokenBytes == null || tokenBytes.Length == 0 || tokenBytes.Length > 1024 * 1024)
                return Failure(Error("SKETCH_REFERENCE_CAPTURE_UNCERTAIN", "Execute", "The sketch was created but SOLIDWORKS returned an invalid persistent reference."));

            var updated = CopyBinding(prepared.Binding);
            updated.NativeReferenceBytes = (byte[])tokenBytes.Clone();
            updated.Status = CadEntityReferenceStatus.Active;
            updated.UpdatedUtc = DateTime.UtcNow;
            try
            {
                await _store.UpdateEntityBindingAsync(updated, token).ConfigureAwait(false);
                return CadCommandResult.Ok(new { entityId = updated.EntityId.ToString("D") });
            }
            catch (Exception ex)
            {
                return Failure(Error("SKETCH_REFERENCE_PERSIST_UNCERTAIN", "Execute", "The sketch was created but its persistent reference could not be stored.", ex.Message));
            }
        }

        internal async Task<SketchReferencePreparation> PrepareSketchResolutionAsync(Guid modelId, Guid entityId, CancellationToken token)
        {
            if (modelId == Guid.Empty || entityId == Guid.Empty)
                return SketchReferencePreparation.Fail(Error("INVALID_IDENTITY_CONTEXT", "Validate", "Resolution requires model and entity IDs."));
            var model = await _store.GetModelAsync(modelId, token).ConfigureAwait(false);
            if (model == null)
                return SketchReferencePreparation.Fail(Error("MODEL_REFERENCE_NOT_FOUND", "Validate", "The managed model ID is not registered."));
            if (model.Status != CadModelIdentityStatus.ActiveUnsaved && model.Status != CadModelIdentityStatus.ActiveSaved)
                return SketchReferencePreparation.Fail(Error("MODEL_REFERENCE_UNAVAILABLE", "Validate", "The managed model is not active."));
            var binding = await _store.GetEntityBindingAsync(modelId, entityId, model.ConfigurationKey, token).ConfigureAwait(false);
            if (binding == null)
                return SketchReferencePreparation.Fail(Error("SKETCH_REFERENCE_NOT_FOUND", "Validate", "The sketch entity ID is not registered for this model and configuration."));
            var error = ValidateSketchBinding(model, binding, entityId, false);
            return error == null ? new SketchReferencePreparation(model, binding) : SketchReferencePreparation.Fail(error);
        }

        internal static CadError ClassifySketchResolution(int nativeStatus, bool validSketch)
        {
            if (nativeStatus == 2) return Error("SKETCH_REFERENCE_SUPPRESSED", "Resolve", "SOLIDWORKS reports that the sketch reference is suppressed.", "nativeStatus=2");
            if (nativeStatus == 4) return Error("SKETCH_REFERENCE_DELETED", "Resolve", "SOLIDWORKS reports that the sketch reference was deleted.", "nativeStatus=4");
            if (nativeStatus != 0) return Error("SKETCH_REFERENCE_INVALID", "Resolve", "SOLIDWORKS could not resolve the sketch reference.", "nativeStatus=" + nativeStatus);
            return validSketch ? null : Error("SKETCH_REFERENCE_KIND_MISMATCH", "Resolve", "The persistent reference did not resolve to a sketch feature.", "nativeStatus=0");
        }

#if SOLIDWORKS_INTEROP
        internal static byte[] CaptureActiveSketch(ModelDoc2 model)
        {
            var sketch = model?.SketchManager?.ActiveSketch;
            var bytes = sketch == null ? null : model.Extension.GetPersistReference3(sketch) as byte[];
            return bytes == null ? null : (byte[])bytes.Clone();
        }

        internal static CadError ResolveOnSta(ModelDoc2 model, byte[] tokenBytes, out int nativeStatus)
        {
            nativeStatus = -1;
            if (model == null || tokenBytes == null || tokenBytes.Length == 0)
                return Error("SKETCH_REFERENCE_INVALID", "Resolve", "The bound model or sketch token is unavailable.");
            try
            {
                var resolved = model.Extension.GetObjectByPersistReference3((byte[])tokenBytes.Clone(), out nativeStatus);
                var sketch = resolved as Sketch;
                var feature = resolved as Feature;
                var validSketch = sketch != null ||
                    (feature != null && feature.GetTypeName2() == "ProfileFeature" && feature.GetSpecificFeature2() is Sketch);
                return ClassifySketchResolution(nativeStatus, validSketch);
            }
            catch (Exception ex)
            {
                return Error("SKETCH_REFERENCE_INVALID", "Resolve", "SOLIDWORKS failed while resolving the sketch reference.", ex.Message);
            }
        }
#endif

        internal static CadCommandResult Failure(CadError error) => new CadCommandResult
        {
            Success = false, Data = new Newtonsoft.Json.Linq.JObject(), Error = error
        };

        private static CadError ValidateSketchBinding(CadModelIdentityRecord model, CadEntityReferenceBinding binding, Guid entityId, bool capture)
        {
            if (binding.ModelId != model.ModelId || binding.EntityId != entityId ||
                !string.Equals(binding.ConfigurationKey, model.ConfigurationKey, StringComparison.Ordinal))
                return Error("SKETCH_REFERENCE_CONTEXT_MISMATCH", "Validate", "The sketch binding belongs to another model, entity or configuration.");
            if (binding.EntityKind != "Sketch" || binding.NativeObjectKind != "SketchFeature" || binding.ReferenceFormatVersion != 3)
                return Error("SKETCH_REFERENCE_KIND_MISMATCH", "Validate", "The entity binding is not a version-3 sketch feature reference.");
            if (capture && binding.Status != CadEntityReferenceStatus.Pending)
                return Error("SKETCH_REFERENCE_NOT_PENDING", "Validate", "The sketch output binding is not pending.");
            if (!capture && (binding.Status != CadEntityReferenceStatus.Active || binding.NativeReferenceBytes == null ||
                binding.NativeReferenceBytes.Length == 0 || binding.NativeReferenceBytes.Length > 1024 * 1024))
                return Error("SKETCH_REFERENCE_INVALID", "Validate", "The sketch binding has no active, valid native token.");
            return null;
        }

        private static CadEntityReferenceBinding CopyBinding(CadEntityReferenceBinding b) => new CadEntityReferenceBinding
        {
            ModelId = b.ModelId, EntityId = b.EntityId, EntityKind = b.EntityKind, ConfigurationKey = b.ConfigurationKey,
            NativeObjectKind = b.NativeObjectKind, ReferenceFormatVersion = b.ReferenceFormatVersion,
            NativeReferenceBytes = b.NativeReferenceBytes == null ? null : (byte[])b.NativeReferenceBytes.Clone(),
            CreatedAtModelRevisionId = b.CreatedAtModelRevisionId, LastResolvedModelRevisionId = b.LastResolvedModelRevisionId,
            SemanticFingerprintJson = b.SemanticFingerprintJson, Status = b.Status, CreatedUtc = b.CreatedUtc, UpdatedUtc = b.UpdatedUtc
        };

        private static CadError Error(string code, string stage, string message, string detail = null) =>
            new CadError { Code = code, Stage = stage, Message = message, Detail = detail };
    }

    internal sealed class SketchReferencePreparation
    {
        internal CadModelIdentityRecord Model { get; }
        internal CadEntityReferenceBinding Binding { get; }
        internal CadError Error { get; }
        internal SketchReferencePreparation(CadModelIdentityRecord model, CadEntityReferenceBinding binding)
        {
            Model = model;
            Binding = binding;
        }
        private SketchReferencePreparation(CadError error) => Error = error;
        internal static SketchReferencePreparation Fail(CadError error) => new SketchReferencePreparation(error);
    }
}
