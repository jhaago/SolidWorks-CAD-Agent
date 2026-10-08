using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    // The trusted sketch ID is resolved and consumed inside one Bridge STA selection scope.
    // This handler is intentionally absent from planner capability advertisement.
    internal sealed class VersionTwoExtrudeCommandHandler : IVersionedCadCommandHandler
    {
        private readonly SolidWorksBridgeFacade _bridge;

        internal VersionTwoExtrudeCommandHandler(SolidWorksBridgeFacade bridge) =>
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

        public string Name => CadCommandNames.Extrude;
        public int OperationVersion => 2;

        public CadError Validate(CadVersionedCommandRequest request)
        {
            var error = CadPlanningCommandContract.Validate(new CadCommandEnvelope
            {
                Command = CadCommandNames.Extrude,
                Parameters = request.Command.Parameters
            });
            if (error != null)
                return new CadError { Code = "INVALID_PARAMETERS", Stage = "Validate", Message = error };
            if (!request.Command.ExecutionId.HasValue || request.Command.ExecutionId.Value == Guid.Empty)
                return new CadError { Code = "MISSING_EXECUTION_ID", Stage = "Validate",
                    Message = "A version-2 native feature requires the Host execution ID." };
            return null;
        }

        public Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken)
        {
            var modelId = request.Command.ManagedModelId.Value;
            var sketchId = request.ProfileSketchEntityId.Value;
            var depthMm = request.Command.Parameters.Value<double>("depthMm");
            return _bridge.WithSelectedSketchAsync(modelId, sketchId, 0, native =>
            {
#if SOLIDWORKS_INTEROP
                return ExtrudeCommandHandler.CreateSelectedBoss((ModelDoc2)native, depthMm);
#else
                return new CadCommandResult
                {
                    Success = false,
                    Error = new CadError { Code = "SOLIDWORKS_INTEROP_UNAVAILABLE", Stage = "Execute", Message = "This build has no SOLIDWORKS interop libraries." }
                };
#endif
            }, cancellationToken, request.Command.ExecutionId);
        }
    }
}
