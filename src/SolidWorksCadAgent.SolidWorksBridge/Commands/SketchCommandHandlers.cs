using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Units;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.SolidWorksBridge.References;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class CreateSketchCommandHandler : SolidWorksCommandHandlerBase
    {
        private static readonly string[] AllowedPlanes = { "Top Plane", "Front Plane", "Right Plane" };
        private readonly SolidWorksReferenceResolver _resolver;

        public CreateSketchCommandHandler(ISolidWorksSession session) : base(session) { }
        public CreateSketchCommandHandler(ISolidWorksSession session, IModelReferenceStore store) : base(session)
        {
            if (store != null) _resolver = new SolidWorksReferenceResolver(store);
        }

        public override string Name => CadCommandNames.CreateSketch;

        public override CadError Validate(JObject parameters)
        {
            var plane = GetString(parameters, "plane");
            if (string.IsNullOrWhiteSpace(plane))
            {
                return InvalidParameter("plane is required.");
            }

            if (Array.IndexOf(AllowedPlanes, plane) < 0)
            {
                return UnsupportedValue("plane must be Top Plane, Front Plane, or Right Plane in V1.");
            }

            return null;
        }

        public override async Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var plane = GetString(parameters, "plane");
            var identity = await Session.InvokeWithApplicationAsync(application =>
            {
                var context = DocumentContext(Session);
                return new { context.ManagedModelId, context.OutputEntityId };
            }, cancellationToken).ConfigureAwait(false);
            SketchReferencePreparation prepared = null;
            if (identity.ManagedModelId.HasValue || identity.OutputEntityId.HasValue)
            {
                if (_resolver == null)
                    return Failure("MODEL_REFERENCE_STORE_UNAVAILABLE", "Validate", "Managed sketch creation requires a reference store.");
                try
                {
                    prepared = await _resolver.PrepareSketchCaptureAsync(identity.ManagedModelId, identity.OutputEntityId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return Failure("SKETCH_REFERENCE_STORE_UNAVAILABLE", "Validate", "The sketch binding could not be checked before creation.", ex.Message);
                }
                if (prepared.Error != null) return SolidWorksReferenceResolver.Failure(prepared.Error);
            }

            SketchCreationOutcome outcome;
            try
            {
                outcome = await Session.InvokeWithApplicationAsync(application =>
                    CreateSketchOnSta(application, plane, prepared), cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentTargetException ex)
            {
                return Failure("DOCUMENT_TARGET_CHANGED", "Execute", ex.Message);
            }
            if (!outcome.Result.Success || prepared == null) return outcome.Result;
            return await _resolver.PersistSketchCaptureAsync(prepared, outcome.ReferenceBytes, CancellationToken.None).ConfigureAwait(false);
        }

        private SketchCreationOutcome CreateSketchOnSta(object application, string plane, SketchReferencePreparation prepared)
        {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                {
                    return SketchCreationOutcome.Fail(Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available for sketch creation."));
                }

                if (prepared != null && (DocumentContext(Session).ManagedModelId != prepared.Model.ModelId ||
                    DocumentContext(Session).OutputEntityId != prepared.Binding.EntityId ||
                    !string.Equals(model.ConfigurationManager.ActiveConfiguration.Name, prepared.Model.ConfigurationKey, StringComparison.Ordinal)))
                    return SketchCreationOutcome.Fail(Failure("SKETCH_REFERENCE_CONTEXT_MISMATCH", "Validate", "The active document, model ID or configuration differs from the prepared sketch binding."));

                var sketchManager = model.SketchManager;
                if (sketchManager.ActiveSketch != null)
                {
                    return SketchCreationOutcome.Fail(Failure("SKETCH_ALREADY_ACTIVE", "Execute", "A sketch is already active. Exit it before creating another sketch."));
                }

                model.ClearSelection2(true);
                var selected = model.Extension.SelectByID2(
                    plane,
                    "PLANE",
                    0.0,
                    0.0,
                    0.0,
                    false,
                    0,
                    null,
                    (int)swSelectOption_e.swSelectOptionDefault);

                if (!selected)
                {
                    return SketchCreationOutcome.Fail(Failure("PLANE_SELECTION_FAILED", "Execute", "SOLIDWORKS could not select the requested reference plane.", plane));
                }

                sketchManager.InsertSketch(true);
                if (sketchManager.ActiveSketch == null)
                {
                    return SketchCreationOutcome.Fail(Failure("SKETCH_CREATION_FAILED", "Execute", "SOLIDWORKS did not enter an active sketch after selecting the plane."));
                }
                if (prepared == null) return SketchCreationOutcome.Ok(Ok(new { plane }), null);
                try
                {
                    var bytes = SolidWorksReferenceResolver.CaptureActiveSketch(model);
                    if (bytes == null || bytes.Length == 0)
                        return SketchCreationOutcome.Fail(Failure("SKETCH_REFERENCE_CAPTURE_UNCERTAIN", "Execute", "The sketch was created but SOLIDWORKS did not return a persistent reference."));
                    return SketchCreationOutcome.Ok(Ok(), bytes);
                }
                catch (Exception ex)
                {
                    return SketchCreationOutcome.Fail(Failure("SKETCH_REFERENCE_CAPTURE_UNCERTAIN", "Execute", "The sketch was created but its persistent reference could not be captured.", ex.Message));
                }
#else
                return SketchCreationOutcome.Fail(InteropUnavailable());
#endif
        }

        private sealed class SketchCreationOutcome
        {
            public CadCommandResult Result { get; private set; }
            public byte[] ReferenceBytes { get; private set; }
            public static SketchCreationOutcome Ok(CadCommandResult result, byte[] bytes) => new SketchCreationOutcome { Result = result, ReferenceBytes = bytes };
            public static SketchCreationOutcome Fail(CadCommandResult result) => new SketchCreationOutcome { Result = result };
        }
    }

    public sealed class AddRectangleCommandHandler : SolidWorksCommandHandlerBase
    {
        public AddRectangleCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.AddRectangle;

        public override CadError Validate(JObject parameters)
        {
            if (!TryGetFiniteDouble(parameters, "centerXmm", out _))
                return InvalidParameter("centerXmm must be a finite number.");
            if (!TryGetFiniteDouble(parameters, "centerYmm", out _))
                return InvalidParameter("centerYmm must be a finite number.");
            if (!TryGetFiniteDouble(parameters, "widthMm", out var width) || width <= 0.0)
                return InvalidParameter("widthMm must be a finite number greater than zero.");
            if (!TryGetFiniteDouble(parameters, "heightMm", out var height) || height <= 0.0)
                return InvalidParameter("heightMm must be a finite number greater than zero.");
            return null;
        }

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            TryGetFiniteDouble(parameters, "centerXmm", out var centerXmm);
            TryGetFiniteDouble(parameters, "centerYmm", out var centerYmm);
            TryGetFiniteDouble(parameters, "widthMm", out var widthMm);
            TryGetFiniteDouble(parameters, "heightMm", out var heightMm);

            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                var sketchManager = model.SketchManager;
                if (sketchManager.ActiveSketch == null)
                    return Failure("NO_ACTIVE_SKETCH", "Execute", "A sketch must be active before adding a rectangle.");

                var centerX = UnitConverter.MillimetresToMetres(centerXmm);
                var centerY = UnitConverter.MillimetresToMetres(centerYmm);
                var halfWidth = UnitConverter.MillimetresToMetres(widthMm) / 2.0;
                var halfHeight = UnitConverter.MillimetresToMetres(heightMm) / 2.0;

                var segments = sketchManager.CreateCenterRectangle(
                    centerX,
                    centerY,
                    0.0,
                    centerX + halfWidth,
                    centerY + halfHeight,
                    0.0);

                if (segments == null)
                    return Failure("RECTANGLE_CREATION_FAILED", "Execute", "SOLIDWORKS did not create the rectangle.");

                return Ok(new { centerXmm, centerYmm, widthMm, heightMm });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class AddCircleCommandHandler : SolidWorksCommandHandlerBase
    {
        public AddCircleCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.AddCircle;

        public override CadError Validate(JObject parameters)
        {
            if (!TryGetFiniteDouble(parameters, "centerXmm", out _))
                return InvalidParameter("centerXmm must be a finite number.");
            if (!TryGetFiniteDouble(parameters, "centerYmm", out _))
                return InvalidParameter("centerYmm must be a finite number.");
            if (!TryGetFiniteDouble(parameters, "diameterMm", out var diameter) || diameter <= 0.0)
                return InvalidParameter("diameterMm must be a finite number greater than zero.");
            return null;
        }

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            TryGetFiniteDouble(parameters, "centerXmm", out var centerXmm);
            TryGetFiniteDouble(parameters, "centerYmm", out var centerYmm);
            TryGetFiniteDouble(parameters, "diameterMm", out var diameterMm);

            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                var sketchManager = model.SketchManager;
                if (sketchManager.ActiveSketch == null)
                    return Failure("NO_ACTIVE_SKETCH", "Execute", "A sketch must be active before adding a circle.");

                var centerX = UnitConverter.MillimetresToMetres(centerXmm);
                var centerY = UnitConverter.MillimetresToMetres(centerYmm);
                var radius = UnitConverter.MillimetresToMetres(diameterMm) / 2.0;
                var segment = sketchManager.CreateCircleByRadius(centerX, centerY, 0.0, radius);

                if (segment == null)
                    return Failure("CIRCLE_CREATION_FAILED", "Execute", "SOLIDWORKS did not create the circle.");

                return Ok(new { centerXmm, centerYmm, diameterMm });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class ExitSketchCommandHandler : SolidWorksCommandHandlerBase
    {
        public ExitSketchCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.ExitSketch;

        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                var sketchManager = model.SketchManager;
                if (sketchManager.ActiveSketch == null)
                    return Failure("NO_ACTIVE_SKETCH", "Execute", "No sketch is active.");

                sketchManager.InsertSketch(true);
                if (sketchManager.ActiveSketch != null)
                    return Failure("SKETCH_EXIT_FAILED", "Execute", "SOLIDWORKS remained in sketch edit mode.");

                return Ok(new { exited = true });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
