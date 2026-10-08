using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge.Commands;
using SolidWorksCadAgent.SolidWorksBridge.References;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge
{
    public sealed class SolidWorksBridgeFacade : ICadCommandExecutor, IVersionedCadCommandExecutor, IDisposable
    {
        private readonly ISolidWorksSession _session;
        private readonly ISolidWorksSession _commandSession;
        private readonly bool _ownsSession;
        private readonly CadCommandRegistry _registry;
        private readonly IModelReferenceStore _modelReferenceStore;
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IReadOnlyCollection<string> RegisteredCommandNames => _registry.CommandNames;

        public SolidWorksBridgeFacade()
            : this(new SolidWorksSession(), new WorkspacePolicy(new AgentSettings().WorkspaceRoot), null, true)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session)
            : this(session, new WorkspacePolicy(new AgentSettings().WorkspaceRoot), null, false)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session, WorkspacePolicy workspacePolicy)
            : this(session, workspacePolicy, null, false)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session, WorkspacePolicy workspacePolicy, IModelReferenceStore modelReferenceStore)
            : this(session, workspacePolicy, modelReferenceStore, false)
        {
        }

        private SolidWorksBridgeFacade(ISolidWorksSession session, WorkspacePolicy workspacePolicy, IModelReferenceStore modelReferenceStore, bool ownsSession)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            if (workspacePolicy == null)
            {
                throw new ArgumentNullException(nameof(workspacePolicy));
            }

            _ownsSession = ownsSession;
            _modelReferenceStore = modelReferenceStore;
            _commandSession = new DocumentScopedSession(_session);
            _registry = new CadCommandRegistry(new ICadCommandHandler[]
            {
                new NewPartCommandHandler(_commandSession),
                new OpenPartCommandHandler(_commandSession, workspacePolicy, modelReferenceStore),
                new SavePartCommandHandler(_commandSession, workspacePolicy),
                new CloseDocumentCommandHandler(_commandSession),
                new CreateSketchCommandHandler(_commandSession, modelReferenceStore),
                new AddLineCommandHandler(_commandSession),
                new AddArcCommandHandler(_commandSession),
                new AddRectangleCommandHandler(_commandSession),
                new AddCircleCommandHandler(_commandSession),
                new AddSlotCommandHandler(_commandSession),
                new AddRegularPolygonCommandHandler(_commandSession),
                new ExitSketchCommandHandler(_commandSession),
                new ExtrudeCommandHandler(_commandSession),
                new CutExtrudeCommandHandler(_commandSession),
                new RebuildCommandHandler(_commandSession),
                new GetBodyCountCommandHandler(_commandSession),
                new GetBoundingBoxCommandHandler(_commandSession),
                new GetFeatureTreeCommandHandler(_commandSession),
                new GetRebuildErrorsCommandHandler(_commandSession)
            }, new IVersionedCadCommandHandler[]
            {
                new VersionTwoExtrudeCommandHandler(this)
            });
        }

        public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));
            }

            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (command != null && (command.ManagedModelId.HasValue || command.OutputEntityId.HasValue) && _modelReferenceStore == null)
                    return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "Managed CAD identity metadata was supplied but no reference store is configured.");
                if (command != null && command.OutputEntityId.HasValue && !command.ManagedModelId.HasValue)
                    return IdentityFailure("INVALID_IDENTITY_CONTEXT", "An output entity ID requires a managed model ID.");
                if (command != null && (command.ExecutionId.HasValue || command.ManagedModelId.HasValue || command.OutputEntityId.HasValue))
                    await _session.InvokeWithApplicationAsync(application =>
                    {
                        var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                        context.BeginExecution(command.ExecutionId);
                        // OpenPart derives its identity from the validated file property and registry.
                        // Keep the current binding intact until BindPendingOpen succeeds.
                        if (command.Command != CadCommandNames.OpenPart &&
                            (command.ManagedModelId.HasValue || command.OutputEntityId.HasValue || command.Command == CadCommandNames.NewPart))
                            context.SetExecutionIdentity(command.ManagedModelId, command.OutputEntityId);
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                return await _registry.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
        }

        public async Task<CadCommandResult> ExecuteVersionedAsync(
            CadVersionedCommandRequest request,
            CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));

            // Operation-version-1 steps in a v2 plan retain the established
            // envelope path. Version-2 consumers go through explicit registry
            // dispatch and cannot fall through to the name-only v1 handlers.
            var legacyCommand = request?.Command?.Command;
            var versionOneFeature = legacyCommand == CadCommandNames.Extrude || legacyCommand == CadCommandNames.CutExtrude;
            if (request != null && (request.PlanVersion == 1 || request.PlanVersion == 2) &&
                request.OperationVersion == 1 && !request.ProfileSketchEntityId.HasValue &&
                !(request.PlanVersion == 2 && versionOneFeature))
                return await ExecuteAsync(request.Command, cancellationToken).ConfigureAwait(false);

            // A registered versioned handler owns its own Bridge command gate while
            // resolving and consuming the sketch in one STA operation. Unsupported
            // versions are rejected by the registry without entering the STA.
            return await _registry.ExecuteVersionedAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Bridge-only reference probe for future consumers. No native object or token leaves this boundary.
        public async Task<CadCommandResult> ResolveSketchReferenceAsync(Guid modelId, Guid entityId, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_modelReferenceStore == null)
                    return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "A reference store is required to resolve a managed sketch.");
                var resolver = new SolidWorksReferenceResolver(_modelReferenceStore);
                SketchReferencePreparation prepared;
                try
                {
                    prepared = await resolver.PrepareSketchResolutionAsync(modelId, entityId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return IdentityFailure("SKETCH_REFERENCE_STORE_UNAVAILABLE", "The sketch binding could not be loaded: " + ex.Message);
                }
                if (prepared.Error != null) return SolidWorksReferenceResolver.Failure(prepared.Error);

                var result = await _session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                    if (context.ManagedModelId != modelId)
                        return IdentityFailure("SKETCH_REFERENCE_CONTEXT_MISMATCH", "The bound document belongs to another managed model.", "Resolve");
                    var app = application as SldWorks;
                    ModelDoc2 model;
                    try { model = context.RequireActive(app?.ActiveDoc) as ModelDoc2; }
                    catch (DocumentTargetException ex) { return IdentityFailure("DOCUMENT_TARGET_CHANGED", ex.Message, "Resolve"); }
                    if (model == null || !string.Equals(model.ConfigurationManager.ActiveConfiguration.Name,
                        prepared.Model.ConfigurationKey, StringComparison.Ordinal))
                        return IdentityFailure("SKETCH_REFERENCE_CONFIGURATION_MISMATCH", "The active document configuration differs from the registered sketch context.", "Resolve");
                    var error = SolidWorksReferenceResolver.ResolveOnSta(model, prepared.Binding.NativeReferenceBytes, out var nativeStatus);
                    if (error != null)
                    {
                        var nativeDetail = error.Detail;
                        if (string.IsNullOrEmpty(nativeDetail)) nativeDetail = "nativeStatus=" + nativeStatus;
                        else if (nativeDetail.IndexOf("nativeStatus=", StringComparison.Ordinal) < 0)
                            nativeDetail = "nativeStatus=" + nativeStatus + "; " + nativeDetail;
                        error.Detail = "modelId=" + modelId.ToString("D") + "; entityId=" + entityId.ToString("D") + "; " + nativeDetail;
                        return SolidWorksReferenceResolver.Failure(error);
                    }
                    return CadCommandResult.Ok(new { modelId = modelId.ToString("D"), entityId = entityId.ToString("D"), nativeStatus, nativeObjectKind = "SketchFeature" });
#else
                    return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build has no SOLIDWORKS interop libraries.");
#endif
                }, cancellationToken).ConfigureAwait(false);
                if (!result.Success) return result;
                prepared.Binding.LastResolvedModelRevisionId = prepared.Model.CurrentModelRevisionId;
                prepared.Binding.UpdatedUtc = DateTime.UtcNow;
                try { await _modelReferenceStore.UpdateEntityBindingAsync(prepared.Binding, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    return IdentityFailure("SKETCH_REFERENCE_EVIDENCE_STORE_FAILED", "The sketch resolved but its resolution evidence could not be stored: " + ex.Message, "Resolve");
                }
                return result;
            }
            finally { _commandGate.Release(); }
        }

        // Internal native-operation boundary: the selected feature and COM model exist only during one STA callback.
        internal async Task<CadCommandResult> WithSelectedSketchAsync(Guid modelId, Guid entityId, int mark,
            Func<object, CadCommandResult> action, CancellationToken cancellationToken,
            Guid? requiredExecutionId = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (mark < 0) return IdentityFailure("SKETCH_SELECTION_INVALID", "Selection mark must be nonnegative.", "Validate");
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_modelReferenceStore == null)
                    return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "A reference store is required to select a managed sketch.");
                SketchReferencePreparation prepared;
                try { prepared = await new SolidWorksReferenceResolver(_modelReferenceStore)
                    .PrepareSketchResolutionAsync(modelId, entityId, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                { return IdentityFailure("SKETCH_REFERENCE_STORE_UNAVAILABLE", "The sketch binding could not be loaded: " + ex.Message); }
                if (prepared.Error != null) return SolidWorksReferenceResolver.Failure(prepared.Error);

                return await _session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                    if (requiredExecutionId.HasValue)
                        context.BeginExecution(requiredExecutionId.Value);
                    if (context.ManagedModelId != modelId)
                        return IdentityFailure("SKETCH_REFERENCE_CONTEXT_MISMATCH", "The bound document belongs to another managed model.", "Resolve");
                    ModelDoc2 model;
                    try { model = context.RequireActive((application as SldWorks)?.ActiveDoc) as ModelDoc2; }
                    catch (DocumentTargetException ex) { return IdentityFailure("DOCUMENT_TARGET_CHANGED", ex.Message, "Resolve"); }
                    if (model == null || !string.Equals(model.ConfigurationManager.ActiveConfiguration.Name,
                        prepared.Model.ConfigurationKey, StringComparison.Ordinal))
                        return IdentityFailure("SKETCH_REFERENCE_CONFIGURATION_MISMATCH", "The active document configuration differs from the registered sketch context.", "Resolve");
                    if (model.SketchManager.ActiveSketch != null)
                        return IdentityFailure("SKETCH_STILL_ACTIVE", "Exit the active sketch before selecting a completed sketch.", "Select");
                    return SolidWorksSketchSelectionScope.Run(model, prepared.Binding.NativeReferenceBytes, mark, native => action(native));
#else
                    return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build has no SOLIDWORKS interop libraries.");
#endif
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
        }

        private static CadCommandResult IdentityFailure(string code, string message, string stage = "Validate") => new CadCommandResult
        {
            Success = false,
            Data = new Newtonsoft.Json.Linq.JObject(),
            Error = new CadError { Code = code, Stage = stage, Message = message }
        };

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsSession)
            {
                _session.Dispose();
            }
        }

        // Separate document binding for each bridge, even when callers share a session.
        private sealed class DocumentScopedSession : ISolidWorksSession
        {
            private readonly ISolidWorksSession _inner;
            public DocumentScopedSession(ISolidWorksSession inner) => _inner = inner;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => _inner.GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => _inner.AttachAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => _inner.LaunchAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken token) =>
                _inner.InvokeWithApplicationAsync(operation, token);
            public void Dispose() { }
        }
    }
}
