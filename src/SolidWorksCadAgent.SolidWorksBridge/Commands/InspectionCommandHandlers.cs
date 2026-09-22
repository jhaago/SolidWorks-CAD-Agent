using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class GetBodyCountCommandHandler : SolidWorksCommandHandlerBase
    {
        public GetBodyCountCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.GetBodyCount;
        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Inspect", "No active SOLIDWORKS document is available.");

                try
                {
                    var bodies = SolidWorksInspector.GetSolidBodies(model);
                    return Ok(new { bodyCount = bodies.Length });
                }
                catch (Exception ex)
                {
                    return Failure("BODY_COUNT_FAILED", "Inspect", "Unable to inspect solid bodies.", ex.Message);
                }
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class GetBoundingBoxCommandHandler : SolidWorksCommandHandlerBase
    {
        public GetBoundingBoxCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.GetBoundingBox;
        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Inspect", "No active SOLIDWORKS document is available.");

                try
                {
                    return Ok(SolidWorksInspector.GetPreciseBoundingBox(model));
                }
                catch (Exception ex)
                {
                    return Failure("BOUNDING_BOX_FAILED", "Inspect", "Unable to calculate precise body extents.", ex.Message);
                }
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class GetFeatureTreeCommandHandler : SolidWorksCommandHandlerBase
    {
        public GetFeatureTreeCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.GetFeatureTree;
        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Inspect", "No active SOLIDWORKS document is available.");

                try
                {
                    return Ok(new { features = SolidWorksInspector.GetFeatureTree(model) });
                }
                catch (Exception ex)
                {
                    return Failure("FEATURE_TREE_FAILED", "Inspect", "Unable to inspect the feature tree.", ex.Message);
                }
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class GetRebuildErrorsCommandHandler : SolidWorksCommandHandlerBase
    {
        public GetRebuildErrorsCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.GetRebuildErrors;
        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                    return Failure("NO_ACTIVE_DOCUMENT", "Inspect", "No active SOLIDWORKS document is available.");

                try
                {
                    return Ok(SolidWorksInspector.RebuildAndInspect(model));
                }
                catch (Exception ex)
                {
                    return Failure("REBUILD_INSPECTION_FAILED", "Inspect", "Unable to rebuild and inspect feature errors.", ex.Message);
                }
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
