using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Units;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class AddSlotCommandHandler : PrismaticProfileCommandHandlerBase
    {
        public AddSlotCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.AddSlot;
        public override CadError Validate(JObject parameters)
        {
            var error = PrismaticProfileGeometry.ValidateSlot(parameters);
            return error == null ? null : InvalidParameter(error);
        }
        protected override IReadOnlyList<CadCommandEnvelope> Primitives(JObject parameters) => PrismaticProfileGeometry.Slot(parameters);
    }

    public sealed class AddRegularPolygonCommandHandler : PrismaticProfileCommandHandlerBase
    {
        public AddRegularPolygonCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.AddRegularPolygon;
        public override CadError Validate(JObject parameters)
        {
            var error = PrismaticProfileGeometry.ValidateRegularPolygon(parameters);
            return error == null ? null : InvalidParameter(error);
        }
        protected override IReadOnlyList<CadCommandEnvelope> Primitives(JObject parameters) => PrismaticProfileGeometry.RegularPolygon(parameters);
    }

    public abstract class PrismaticProfileCommandHandlerBase : SolidWorksCommandHandlerBase
    {
        protected PrismaticProfileCommandHandlerBase(ISolidWorksSession session) : base(session) { }
        protected abstract IReadOnlyList<CadCommandEnvelope> Primitives(JObject parameters);

        public override Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken token) => InvokeAsync(application =>
        {
#if SOLIDWORKS_INTEROP
            var model = RequireDocument(application) as ModelDoc2;
            if (model == null) return Failure("NO_ACTIVE_DOCUMENT", "Execute", "An owned SOLIDWORKS document is required.");
            var manager = model.SketchManager;
            if (manager.ActiveSketch == null) return Failure("NO_ACTIVE_SKETCH", "Execute", "An owned sketch must be active before adding a profile.");
            var primitives = Primitives(parameters);
            var previous = manager.AddToDB;
            try
            {
                // Preserve explicit coordinates without SOLIDWORKS inferencing or snapping.
                manager.AddToDB = true;
                foreach (var command in primitives)
                {
                    token.ThrowIfCancellationRequested();
                    var p = command.Parameters;
                    object segment;
                    if (command.Command == CadCommandNames.AddLine)
                        segment = manager.CreateLine(Mm(p, "startXmm"), Mm(p, "startYmm"), 0,
                            Mm(p, "endXmm"), Mm(p, "endYmm"), 0);
                    else if (command.Command == CadCommandNames.AddArc)
                        segment = manager.CreateArc(Mm(p, "centerXmm"), Mm(p, "centerYmm"), 0,
                            Mm(p, "startXmm"), Mm(p, "startYmm"), 0,
                            Mm(p, "endXmm"), Mm(p, "endYmm"), 0,
                            (short)((bool)p["clockwise"] ? -1 : 1));
                    else
                        return Failure("PROFILE_PRIMITIVE_UNSUPPORTED", "Execute", "The profile contains an unsupported primitive.");
                    if (segment == null)
                        return Failure("PROFILE_CREATION_FAILED", "Execute", "SOLIDWORKS did not create every profile segment.");
                }
                return Ok(new { created = true, segmentCount = primitives.Count, geometry = parameters.DeepClone() });
            }
            finally { manager.AddToDB = previous; }
#else
            return InteropUnavailable();
#endif
        }, token);
        private static double Mm(JObject p, string name) => UnitConverter.MillimetresToMetres((double)p[name]);
    }
}
