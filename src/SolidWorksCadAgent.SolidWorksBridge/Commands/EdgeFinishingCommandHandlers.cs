using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Units;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class FilletEdgesCommandHandler : EdgeFinishingCommandHandlerBase
    {
        public FilletEdgesCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.FilletEdges;
        protected override bool IsFillet => true;
        public override CadError Validate(JObject parameters)
        {
            var error = FaceFeatureValidation.ValidateFillet(parameters);
            return error == null ? null : InvalidParameter(error);
        }
    }

    public sealed class ChamferEdgesCommandHandler : EdgeFinishingCommandHandlerBase
    {
        public ChamferEdgesCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.ChamferEdges;
        protected override bool IsFillet => false;
        public override CadError Validate(JObject parameters)
        {
            var error = FaceFeatureValidation.ValidateChamfer(parameters);
            return error == null ? null : InvalidParameter(error);
        }
    }

    public abstract class EdgeFinishingCommandHandlerBase : SolidWorksCommandHandlerBase
    {
        protected EdgeFinishingCommandHandlerBase(ISolidWorksSession session) : base(session) { }
        protected abstract bool IsFillet { get; }
        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken token) => InvokeAsync(application =>
        {
#if SOLIDWORKS_INTEROP
            var model = RequireDocument(application) as ModelDoc2;
            if (model == null) return Failure("NO_ACTIVE_DOCUMENT", "Execute", "An owned SOLIDWORKS document is required.");
            if (model.SketchManager.ActiveSketch != null)
                return Failure("SKETCH_STILL_ACTIVE", "Execute", "Exit the sketch before finishing edges.");
            var axis = (string)parameters["normalAxis"];
            var side = (string)parameters["side"];
            var sizeName = IsFillet ? "radiusMm" : "distanceMm";
            var sizeMm = (double)parameters[sizeName];
            var sizeMetres = UnitConverter.MillimetresToMetres(sizeMm);
            model.ClearSelection2(true);
            try
            {
                var face = NativeFaceSelector.Resolve(model, axis, side);
                var edges = NativeFaceSelector.OuterEdges(face);
                if (edges == null || edges.Count == 0)
                    return Failure("NO_OUTER_EDGES", "Execute", "The selected face has no supported outer-loop edges.");
                var mark = IsFillet ? 1 : 0;
                var selectionManager = (SelectionMgr)model.SelectionManager;
                var selection = selectionManager.CreateSelectData();
                selection.Mark = mark;
                foreach (var edge in edges)
                {
                    token.ThrowIfCancellationRequested();
                    if (edge == null || !((IEntity)edge).Select4(true, selection))
                        return Failure("EDGE_SELECTION_FAILED", "Execute", "SOLIDWORKS could not select every intended outer-loop edge.");
                }
                if (selectionManager.GetSelectedObjectCount2(mark) != edges.Count)
                    return Failure("EDGE_SELECTION_FAILED", "Execute", "The selected edge count does not match the intended outer loop.");
                token.ThrowIfCancellationRequested();
                Feature feature;
                if (IsFillet)
                {
                    // Only uniform-radius simple fillets. Never enable swFeatureFilletPropagate.
                    feature = model.FeatureManager.FeatureFillet3(
                        (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius,
                        sizeMetres, 0.0, 0.0,
                        (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
                        (int)swFilletOverFlowType_e.swFilletOverFlowType_Default,
                        (int)swFeatureFilletProfileType_e.swFeatureFilletCircular,
                        null, null, null, null, null, null, null) as Feature;
                }
                else
                {
                    // Options=0 excludes tangent propagation; 45 degrees gives equal legs.
                    feature = model.FeatureManager.InsertFeatureChamfer(
                        0, (int)swChamferType_e.swChamferAngleDistance,
                        sizeMetres, Math.PI / 4.0, 0.0, 0.0, 0.0, 0.0);
                }
                if (feature == null)
                    return Failure(IsFillet ? "FILLET_CREATION_FAILED" : "CHAMFER_CREATION_FAILED", "Execute", "SOLIDWORKS did not create the requested edge finishing feature.");
                var result = new JObject { ["normalAxis"] = axis, ["side"] = side, ["edgeCount"] = edges.Count, ["featureName"] = feature.Name, [sizeName] = sizeMm };
                if (!IsFillet) result["angleDegrees"] = 45.0;
                return Ok(result);
            }
            finally { model.ClearSelection2(true); }
#else
            return InteropUnavailable();
#endif
        }, token);
    }
}
