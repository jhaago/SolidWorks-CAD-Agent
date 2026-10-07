using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Units;
using SolidWorksCadAgent.Core.Commands;
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
            var error = CadPlanningCommandContract.Validate(new CadCommandEnvelope
            {
                Command = CadCommandNames.Extrude,
                Parameters = parameters
            });
            return error == null ? null : InvalidParameter(error);
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
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                if (model.SketchManager.ActiveSketch != null)
                    return Failure("SKETCH_STILL_ACTIVE", "Execute", "Exit the sketch before creating an extrusion feature.");

                var depth = UnitConverter.MillimetresToMetres(depthMm);
                var feature = model.FeatureManager.FeatureExtrusion2(
                    true,
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
            var error = PrismaticProfileGeometry.ValidateCut(parameters);
            return error == null ? null : InvalidParameter(error);
        }

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var endCondition = GetString(parameters, "endCondition");
            bool blind = endCondition == "Blind";
            double? depthMm = blind ? (double?)parameters["depthMm"] : null;
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available.");

                if (model.SketchManager.ActiveSketch != null)
                    return Failure("SKETCH_STILL_ACTIVE", "Execute", "Exit the sketch before creating a cut feature.");

                // FeatureCut4's long signature is isolated here so the version-independent
                // command contract remains stable. The V1 plate workflow creates its boss
                // along the sketch normal, while SOLIDWORKS defaults cuts opposite that
                // normal, so Direction 1 is reversed to cut into the newly created body.
                var feature = model.FeatureManager.FeatureCut4(
                    true,
                    false,
                    true,
                    blind ? (int)swEndConditions_e.swEndCondBlind : (int)swEndConditions_e.swEndCondThroughAll,
                    (int)swEndConditions_e.swEndCondBlind,
                    blind ? UnitConverter.MillimetresToMetres(depthMm.Value) : 0.0,
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
                    return Failure("CUT_EXTRUDE_FAILED", "Execute", "SOLIDWORKS did not create the requested cut.");

                return Ok(new { endCondition, depthMm, featureName = feature.Name });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
