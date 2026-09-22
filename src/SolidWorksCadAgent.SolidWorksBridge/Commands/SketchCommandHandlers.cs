using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Units;
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

        public CreateSketchCommandHandler(ISolidWorksSession session) : base(session) { }

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

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var plane = GetString(parameters, "plane");
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available for sketch creation.");
                }

                var sketchManager = model.SketchManager;
                if (sketchManager.ActiveSketch != null)
                {
                    return Failure("SKETCH_ALREADY_ACTIVE", "Execute", "A sketch is already active. Exit it before creating another sketch.");
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
                    return Failure("PLANE_SELECTION_FAILED", "Execute", "SOLIDWORKS could not select the requested reference plane.", plane);
                }

                sketchManager.InsertSketch(true);
                if (sketchManager.ActiveSketch == null)
                {
                    return Failure("SKETCH_CREATION_FAILED", "Execute", "SOLIDWORKS did not enter an active sketch after selecting the plane.");
                }

                return Ok(new { plane });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
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
                var model = swApp?.ActiveDoc as ModelDoc2;
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
                var model = swApp?.ActiveDoc as ModelDoc2;
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
                var model = swApp?.ActiveDoc as ModelDoc2;
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
