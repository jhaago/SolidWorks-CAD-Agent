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
    public sealed class ExtrudeCommandHandler : SolidWorksCommandHandlerBase
    {
        public ExtrudeCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.Extrude;

        public override CadError Validate(JObject parameters)
        {
            if (!TryGetFiniteDouble(parameters, "depthMm", out var depth) || depth <= 0.0)
            {
                return InvalidParameter("depthMm must be a finite number greater than zero.");
            }

            return null;
        }

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            TryGetFiniteDouble(parameters, "depthMm", out var depthMm);
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                if (model.SketchManager.ActiveSketch != null)
                    return Failure("SKETCH_STILL_ACTIVE", "Execute", "Exit the sketch before creating an extrusion feature.");

                var depth = UnitConverter.MillimetresToMetres(depthMm);
                var feature = model.FeatureManager.FeatureExtrusion2(
                    false,
                    false,
                    false,
                    (int)swEndConditions_e.swEndCondBlind,
                    (int)swEndConditions_e.swEndCondBlind,
                    depth,
                    depth,
                    false,
                    false,
                    false,
                    false,
                    0.0,
                    0.0,
                    false,
                    false,
                    false,
                    false,
                    true,
                    true,
                    true,
                    (int)swStartConditions_e.swStartSketchPlane,
                    0.0,
                    false);

                if (feature == null)
                    return Failure("EXTRUDE_FAILED", "Execute", "SOLIDWORKS did not create the boss extrusion.");

                return Ok(new { depthMm, featureName = feature.Name });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class CutExtrudeCommandHandler : SolidWorksCommandHandlerBase
    {
        public CutExtrudeCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.CutExtrude;

        public override CadError Validate(JObject parameters)
        {
            var endCondition = GetString(parameters, "endCondition");
            if (string.IsNullOrWhiteSpace(endCondition))
            {
                return InvalidParameter("endCondition is required.");
            }

            if (endCondition != "ThroughAll")
            {
                return UnsupportedValue("V1 CutExtrude supports only endCondition=ThroughAll.");
            }

            return null;
        }

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

                if (model.SketchManager.ActiveSketch != null)
                    return Failure("SKETCH_STILL_ACTIVE", "Execute", "Exit the sketch before creating a cut feature.");

                // FeatureCut4's long signature is isolated here so the version-independent
                // command contract remains stable. The values mirror the SOLIDWORKS 2020
                // API definition: Direction 1 Through All, no draft/thin feature, automatic
                // feature scope, start from the sketch plane, and no sheet-metal normal cut.
                var feature = model.FeatureManager.FeatureCut4(
                    true,
                    false,
                    false,
                    (int)swEndConditions_e.swEndCondThroughAll,
                    (int)swEndConditions_e.swEndCondBlind,
                    0.0,
                    0.0,
                    false,
                    false,
                    false,
                    false,
                    0.0,
                    0.0,
                    false,
                    false,
                    false,
                    false,
                    false,
                    true,
                    true,
                    true,
                    true,
                    false,
                    (int)swStartConditions_e.swStartSketchPlane,
                    0.0,
                    false,
                    false);

                if (feature == null)
                    return Failure("CUT_EXTRUDE_FAILED", "Execute", "SOLIDWORKS did not create the through-all cut.");

                return Ok(new { endCondition = "ThroughAll", featureName = feature.Name });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
