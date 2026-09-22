using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class NewPartCommandHandler : SolidWorksCommandHandlerBase
    {
        public NewPartCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.NewPart;

        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                if (swApp == null)
                {
                    return Failure("SOLIDWORKS_APPLICATION_INVALID", "Execute", "The connected SOLIDWORKS application object is invalid.");
                }

                var template = swApp.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);

                if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                {
                    return Failure(
                        "PART_TEMPLATE_NOT_CONFIGURED",
                        "Execute",
                        "SOLIDWORKS does not have a valid default part template configured.",
                        template);
                }

                var model = swApp.NewDocument(template, 0, 0.0, 0.0) as ModelDoc2;
                if (model == null)
                {
                    return Failure("NEW_PART_FAILED", "Execute", "SOLIDWORKS did not create a new part document.");
                }

                return Ok(new { documentTitle = model.GetTitle(), template });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class RebuildCommandHandler : SolidWorksCommandHandlerBase
    {
        public RebuildCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.Rebuild;

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
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Execute", "No active SOLIDWORKS document is available to rebuild.");
                }

                var rebuilt = model.ForceRebuild3(false);
                if (!rebuilt)
                {
                    return Failure("REBUILD_FAILED", "Execute", "SOLIDWORKS reported that the rebuild failed.");
                }

                return Ok(new { rebuilt = true });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
