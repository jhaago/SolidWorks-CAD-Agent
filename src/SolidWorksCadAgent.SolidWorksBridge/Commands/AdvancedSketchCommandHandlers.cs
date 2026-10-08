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
    public sealed class AddLineCommandHandler : SolidWorksCommandHandlerBase
    {
        public AddLineCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.AddLine;
        public override CadError Validate(JObject parameters)
        {
            var error = SketchPrimitiveValidation.ValidateLine(parameters);
            return error == null ? null : InvalidParameter(error);
        }
        public override Task<CadCommandResult> ExecuteAsync(JObject p, CancellationToken token) => InvokeAsync(application =>
        {
#if SOLIDWORKS_INTEROP
            var model = RequireDocument(application) as ModelDoc2;
            var manager = model?.SketchManager;
            if (manager?.ActiveSketch == null) return Failure("NO_ACTIVE_SKETCH", "Execute", "An owned sketch must be active before adding a line.");
            var previous = manager.AddToDB;
            try
            {
                manager.AddToDB = true; // Avoid inferencing/snapping that can change explicit coordinates.
                var segment = manager.CreateLine(Mm(p, "startXmm"), Mm(p, "startYmm"), 0,
                    Mm(p, "endXmm"), Mm(p, "endYmm"), 0);
                return segment == null ? Failure("LINE_CREATION_FAILED", "Execute", "SOLIDWORKS did not create the line.")
                    : Ok(new { created = true, geometry = p.DeepClone() });
            }
            finally { manager.AddToDB = previous; }
#else
            return InteropUnavailable();
#endif
        }, token);
        private static double Mm(JObject p, string name) => UnitConverter.MillimetresToMetres((double)p[name]);
    }

    public sealed class AddArcCommandHandler : SolidWorksCommandHandlerBase
    {
        public AddArcCommandHandler(ISolidWorksSession session) : base(session) { }
        public override string Name => CadCommandNames.AddArc;
        public override CadError Validate(JObject parameters)
        {
            var error = SketchPrimitiveValidation.ValidateArc(parameters);
            return error == null ? null : InvalidParameter(error);
        }
        public override Task<CadCommandResult> ExecuteAsync(JObject p, CancellationToken token) => InvokeAsync(application =>
        {
#if SOLIDWORKS_INTEROP
            var model = RequireDocument(application) as ModelDoc2;
            var manager = model?.SketchManager;
            if (manager?.ActiveSketch == null) return Failure("NO_ACTIVE_SKETCH", "Execute", "An owned sketch must be active before adding an arc.");
            var previous = manager.AddToDB;
            try
            {
                manager.AddToDB = true;
                var segment = manager.CreateArc(Mm(p, "centerXmm"), Mm(p, "centerYmm"), 0,
                    Mm(p, "startXmm"), Mm(p, "startYmm"), 0, Mm(p, "endXmm"), Mm(p, "endYmm"), 0,
                    (short)((bool)p["clockwise"] ? -1 : 1));
                return segment == null ? Failure("ARC_CREATION_FAILED", "Execute", "SOLIDWORKS did not create the arc.")
                    : Ok(new { created = true, geometry = p.DeepClone() });
            }
            finally { manager.AddToDB = previous; }
#else
            return InteropUnavailable();
#endif
        }, token);
        private static double Mm(JObject p, string name) => UnitConverter.MillimetresToMetres((double)p[name]);
    }
}