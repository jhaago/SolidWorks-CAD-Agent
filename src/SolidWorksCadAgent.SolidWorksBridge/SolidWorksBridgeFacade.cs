using System;
using System.Linq;
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
    public sealed partial class SolidWorksBridgeFacade : ICadCommandExecutor, IVersionedCadCommandExecutor, ICadOperationAvailability, ICheckpointedParametricEditExecutor, IDisposable
    {
        private readonly ISolidWorksSession _session;
        private readonly ISolidWorksSession _commandSession;
        private readonly bool _ownsSession;
        private readonly CadCommandRegistry _registry;
        private readonly IModelReferenceStore _modelReferenceStore;
        private readonly SavePartCommandHandler _savePartHandler;
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IReadOnlyCollection<string> RegisteredCommandNames => _registry.CommandNames;

        public bool SupportsManagedReferences
        {
            get
            {
#if SOLIDWORKS_INTEROP
                return !_disposed && _modelReferenceStore != null;
#else
                return false;
#endif
            }
        }

        public bool IsOperationRegistered(string commandName, int operationVersion)
        {
#if SOLIDWORKS_INTEROP
            return !_disposed && _registry.IsOperationRegistered(commandName, operationVersion);
#else
            return false;
#endif
        }

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
            _editWorkspace = workspacePolicy;
            _modelReferenceStore = modelReferenceStore;
            _commandSession = new DocumentScopedSession(_session);
            _savePartHandler = new SavePartCommandHandler(_commandSession, workspacePolicy);
            _registry = new CadCommandRegistry(new ICadCommandHandler[]
            {
                new NewPartCommandHandler(_commandSession),
                new OpenPartCommandHandler(_commandSession, workspacePolicy, modelReferenceStore),
                _savePartHandler,
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
                new GetRebuildErrorsCommandHandler(_commandSession),
                new GetPrismaticCutReadbackCommandHandler(_commandSession),
                new TopologyInspectionCommandHandler(_commandSession),
                new FaceBoundaryInspectionCommandHandler(_commandSession),
                new SketchConstraintGraphCommandHandler(),
                new ComponentPatternPlanCommandHandler(),
                new StandardDrawingLayoutCommandHandler()
            }, new IVersionedCadCommandHandler[]
            {
                new VersionTwoExtrudeCommandHandler(this),
                new ParametricRectangleCommandHandler(this),
                new ParametricCircleCommandHandler(this),
                new ParametricExtrudeCommandHandler(this),
                new VersionTwoCutCommandHandler(this)
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
                {
                    var identityMatches = await _session.InvokeWithApplicationAsync(application =>
                    {
                        var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                        context.BeginExecution(command.ExecutionId);
                        if (command.ManagedModelId.HasValue && command.Command != CadCommandNames.NewPart &&
                            command.Command != CadCommandNames.OpenPart && context.ManagedModelId != command.ManagedModelId &&
                            !(command.Command == CadCommandNames.CreateSketch && !context.ManagedModelId.HasValue))
                            return false;
                        // OpenPart derives its identity from the validated file property and registry.
                        // Keep the current binding intact until BindPendingOpen succeeds.
                        if (command.Command != CadCommandNames.OpenPart &&
                            (command.ManagedModelId.HasValue || command.OutputEntityId.HasValue || command.Command == CadCommandNames.NewPart))
                            context.SetExecutionIdentity(command.ManagedModelId, command.OutputEntityId);
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                    if (!identityMatches)
                        return IdentityFailure("DOCUMENT_IDENTITY_MISMATCH", "The bound document belongs to another managed model.");
                }
                if (command?.Command == "InspectParametricPlate")
                    return await InspectParametricPlateAsync(command, cancellationToken).ConfigureAwait(false);
                if (command?.Command == "InspectParametricHole")
                    return await InspectParametricPlateAsync(command, cancellationToken, true).ConfigureAwait(false);
                return await _registry.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
        }

        private static bool IsSketchPrimitive(string command) =>
            command == CadCommandNames.AddRectangle || command == CadCommandNames.AddCircle ||
            command == CadCommandNames.AddSlot || command == CadCommandNames.AddRegularPolygon ||
            command == CadCommandNames.AddLine || command == CadCommandNames.AddArc;
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
            if (request?.PlanVersion == 2 && request.OperationVersion == 2 && legacyCommand == CadCommandNames.AddRectangle)
            {
                var handler = new ParametricRectangleCommandHandler(this);
                var error = handler.Validate(request);
                if (error != null) return new CadCommandResult { Success = false, Error = error, Data = new Newtonsoft.Json.Linq.JObject() };
                return await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            if (request?.PlanVersion == 2 && request.OperationVersion == 2 && legacyCommand == CadCommandNames.AddCircle)
            {
                var handler = new ParametricCircleCommandHandler(this);
                var error = handler.Validate(request);
                if (error != null) return new CadCommandResult { Success = false, Error = error, Data = new Newtonsoft.Json.Linq.JObject() };
                return await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            var versionOneFeature = legacyCommand == CadCommandNames.Extrude || legacyCommand == CadCommandNames.CutExtrude;
            if (request?.PlanVersion == 2 && request.OperationVersion == 1 &&
                IsSketchPrimitive(legacyCommand))
            {
                if (!request.TargetSketchEntityId.HasValue || request.TargetSketchEntityId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_SKETCH_REFERENCE", "A version-2 sketch primitive requires the Host-bound active sketch ID.");
                if (!request.Command.ManagedModelId.HasValue || request.Command.ManagedModelId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_MODEL_REFERENCE", "A version-2 sketch primitive requires the Host-managed model ID.");
                if (!request.Command.ExecutionId.HasValue || request.Command.ExecutionId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_EXECUTION_ID", "A version-2 sketch primitive requires the Host execution ID.");
                if (request.ProfileSketchEntityId.HasValue || request.Command.OutputEntityId.HasValue)
                    return IdentityFailure("INVALID_EXECUTION_CONTEXT", "A sketch primitive cannot bind a profile or output entity ID.");
                var parameterError = CadPlanningCommandContract.Validate(request.Command);
                if (parameterError != null)
                    return IdentityFailure("INVALID_PARAMETERS", parameterError);
                var parameters = request.Command.Parameters;
                return await WithActiveSketchAsync(request.Command.ManagedModelId.Value,
                    request.TargetSketchEntityId.Value,
                    native =>
                    {
#if SOLIDWORKS_INTEROP
                        var model = (ModelDoc2)native;
                        switch (legacyCommand)
                        {
                            case CadCommandNames.AddRectangle:
                                return AddRectangleCommandHandler.CreateOnActiveSketch(model,
                                    parameters.Value<double>("centerXmm"), parameters.Value<double>("centerYmm"),
                                    parameters.Value<double>("widthMm"), parameters.Value<double>("heightMm"));
                            case CadCommandNames.AddCircle:
                                return AddCircleCommandHandler.CreateOnActiveSketch(model,
                                    parameters.Value<double>("centerXmm"), parameters.Value<double>("centerYmm"), parameters.Value<double>("diameterMm"));
                            case CadCommandNames.AddLine: return AddLineCommandHandler.CreateOnActiveSketch(model, parameters);
                            case CadCommandNames.AddArc: return AddArcCommandHandler.CreateOnActiveSketch(model, parameters);
                            case CadCommandNames.AddSlot:
                                return PrismaticProfileCommandHandlerBase.CreateOnActiveSketch(model, parameters, PrismaticProfileGeometry.Slot(parameters), cancellationToken);
                            case CadCommandNames.AddRegularPolygon:
                                return PrismaticProfileCommandHandlerBase.CreateOnActiveSketch(model, parameters, PrismaticProfileGeometry.RegularPolygon(parameters), cancellationToken);
                            default: return IdentityFailure("UNSUPPORTED_COMMAND", "Unsupported sketch primitive.");
                        }
#else
                        return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build has no SOLIDWORKS interop libraries.");
#endif
                    }, cancellationToken, request.Command.ExecutionId.Value).ConfigureAwait(false);
            }
            if (request?.PlanVersion == 2 && request.OperationVersion == 1 &&
                legacyCommand == CadCommandNames.ExitSketch)
            {
                if (!request.TargetSketchEntityId.HasValue || request.TargetSketchEntityId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_SKETCH_REFERENCE", "A version-2 ExitSketch requires the Host-bound active sketch ID.");
                if (!request.Command.ManagedModelId.HasValue || request.Command.ManagedModelId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_MODEL_REFERENCE", "A version-2 ExitSketch requires the Host-managed model ID.");
                if (!request.Command.ExecutionId.HasValue || request.Command.ExecutionId.Value == Guid.Empty)
                    return IdentityFailure("MISSING_EXECUTION_ID", "A version-2 ExitSketch requires the Host execution ID.");
                if (request.ProfileSketchEntityId.HasValue || request.Command.OutputEntityId.HasValue)
                    return IdentityFailure("INVALID_EXECUTION_CONTEXT", "ExitSketch cannot bind a profile or output entity ID.");
                var parameterError = CadPlanningCommandContract.Validate(request.Command);
                if (parameterError != null)
                    return IdentityFailure("INVALID_PARAMETERS", parameterError);
                return await WithActiveSketchAsync(request.Command.ManagedModelId.Value,
                    request.TargetSketchEntityId.Value,
                    native =>
                    {
#if SOLIDWORKS_INTEROP
                        return ExitSketchCommandHandler.ExitActiveSketch((ModelDoc2)native);
#else
                        return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build has no SOLIDWORKS interop libraries.");
#endif
                    }, cancellationToken, request.Command.ExecutionId.Value, verifyClosedReference: true).ConfigureAwait(false);
            }
            if (request?.PlanVersion == 2 && request.OperationVersion == 1 &&
                legacyCommand == CadCommandNames.SavePart)
            {
                if (!request.Command.ManagedModelId.HasValue || request.Command.ManagedModelId.Value == Guid.Empty ||
                    !request.Command.ExecutionId.HasValue || request.Command.ExecutionId.Value == Guid.Empty ||
                    request.Command.OutputEntityId.HasValue || request.TargetSketchEntityId.HasValue ||
                    request.ProfileSketchEntityId.HasValue ||
                    request.Command.Parameters?.Value<bool?>("allowOverwrite") == true)
                    return IdentityFailure("INVALID_EXECUTION_CONTEXT", "A version-2 save requires a managed model, execution ID and non-overwriting target.");
                var parameterError = CadPlanningCommandContract.Validate(request.Command);
                if (parameterError != null) return IdentityFailure("INVALID_PARAMETERS", parameterError);
                await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_modelReferenceStore == null)
                        return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "A managed reference store is required for version-2 save.");
                    var identityMatches = await _session.InvokeWithApplicationAsync(application =>
                    {
                        var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                        context.BeginExecution(request.Command.ExecutionId);
                        return context.ManagedModelId == request.Command.ManagedModelId;
                    }, cancellationToken).ConfigureAwait(false);
                    if (!identityMatches)
                        return IdentityFailure("DOCUMENT_IDENTITY_MISMATCH", "The bound document belongs to another managed model.");
                    return await _savePartHandler.ExecuteStagedV2Async(request.Command.Parameters,
                        request.Command.ManagedModelId.Value, cancellationToken).ConfigureAwait(false);
                }
                finally { _commandGate.Release(); }
            }
            if (request != null && (request.PlanVersion == 1 || request.PlanVersion == 2) &&
                request.OperationVersion == 1 && !request.ProfileSketchEntityId.HasValue && !request.TargetSketchEntityId.HasValue &&
                !(request.PlanVersion == 2 && versionOneFeature))
                return await ExecuteAsync(request.Command, cancellationToken).ConfigureAwait(false);

            // A registered versioned handler owns its own Bridge command gate while
            // resolving and consuming the sketch in one STA operation. Unsupported
            // versions are rejected by the registry without entering the STA.
            return await _registry.ExecuteVersionedAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<CadCommandResult> InspectParametricPlateAsync(CadCommandEnvelope command, CancellationToken token, bool hole = false)
        {
            if (_modelReferenceStore == null || !command.ManagedModelId.HasValue || !command.ExecutionId.HasValue ||
                !Guid.TryParse(command.Parameters?.Value<string>("sketchEntityId"), out var sketchId) || sketchId == Guid.Empty)
                return IdentityFailure("INVALID_EXECUTION_CONTEXT", "Trusted parametric inspection identities required.");
            try
            {
                var identity = await _modelReferenceStore.GetModelAsync(command.ManagedModelId.Value, token).ConfigureAwait(false);
                if (identity == null) return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "Managed model missing.");
                var bindings = new System.Collections.Generic.List<CadEntityReferenceBinding>();
                bindings.Add(await _modelReferenceStore.GetEntityBindingAsync(identity.ModelId, sketchId, identity.ConfigurationKey, token).ConfigureAwait(false));
                foreach (var role in hole ? Core.References.CadParametricIdentity.HoleRoles : Core.References.CadParametricIdentity.Roles)
                    bindings.Add(await _modelReferenceStore.GetEntityBindingAsync(identity.ModelId, Core.References.CadParametricIdentity.For(identity.ModelId, sketchId, role), identity.ConfigurationKey, token).ConfigureAwait(false));
                if (bindings.Any(b => b == null)) return IdentityFailure("PARAMETRIC_BINDING_MISSING", "Required parameter or segment binding missing.", "Inspect");
                return await _session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var context = SolidWorksCommandHandlerBase.DocumentContext(_commandSession);
                    var model = context.RequireActive((application as SldWorks)?.ActiveDoc);
                    if (hole) return CadCommandResult.Ok(new { parametricHole = Parametric.ParametricHoleInspector.Inspect(model, identity, sketchId, bindings) });
                    return CadCommandResult.Ok(new { parametricPlate = Parametric.ParametricPlateInspector.Inspect(model, identity, sketchId, bindings) });
#else
                    return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "Native interop required.");
#endif
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            { return IdentityFailure("PARAMETRIC_INSPECTION_FAILED", ex.Message, "Inspect"); }
        }

        // Bridge-only reference probe for future consumers. No native object or token leaves this boundary.
        internal async Task<CadCommandResult> WithActiveSketchAsync(Guid modelId, Guid entityId,
            Func<object, CadCommandResult> action, CancellationToken cancellationToken, Guid executionId,
            bool verifyClosedReference = false, Func<object, object, CadCommandResult> applicationAction = null)
        {
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_modelReferenceStore == null)
                    return IdentityFailure("MODEL_REFERENCE_STORE_UNAVAILABLE", "A reference store is required for a version-2 sketch mutation.");
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
                    context.BeginExecution(executionId);
                    if (context.ManagedModelId != modelId)
                        return IdentityFailure("SKETCH_REFERENCE_CONTEXT_MISMATCH", "The bound document belongs to another managed model.", "Resolve");
                    ModelDoc2 model;
                    try { model = context.RequireActive((application as SldWorks)?.ActiveDoc) as ModelDoc2; }
                    catch (DocumentTargetException ex) { return IdentityFailure("DOCUMENT_TARGET_CHANGED", ex.Message, "Resolve"); }
                    if (model == null || !string.Equals(model.ConfigurationManager.ActiveConfiguration.Name,
                        prepared.Model.ConfigurationKey, StringComparison.Ordinal))
                        return IdentityFailure("SKETCH_REFERENCE_CONFIGURATION_MISMATCH", "The active document configuration differs from the registered sketch context.", "Resolve");
                    return SolidWorksSketchSelectionScope.RunActive(model, prepared.Binding.NativeReferenceBytes,
                        native =>
                        {
                            var result = applicationAction == null ? action(native) : applicationAction(application, native);
                            if (!verifyClosedReference || result == null || !result.Success) return result;
                            if (model.SketchManager.ActiveSketch != null)
                                return IdentityFailure("SKETCH_EXIT_FAILED", "SOLIDWORKS remained in sketch edit mode.", "Verify");
                            var error = SolidWorksReferenceResolver.ResolveOnSta(model,
                                prepared.Binding.NativeReferenceBytes, out _);
                            return error == null ? result : SolidWorksReferenceResolver.Failure(error);
                        });
#else
                    return IdentityFailure("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build has no SOLIDWORKS interop libraries.");
#endif
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
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
