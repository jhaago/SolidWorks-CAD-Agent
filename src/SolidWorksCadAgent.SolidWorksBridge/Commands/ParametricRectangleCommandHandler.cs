using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    internal sealed class ParametricRectangleCommandHandler:IVersionedCadCommandHandler
    {
        private readonly SolidWorksBridgeFacade _bridge;
        internal ParametricRectangleCommandHandler(SolidWorksBridgeFacade bridge)=>_bridge=bridge;
        public string Name=>CadCommandNames.AddRectangle;
        public int OperationVersion=>2;
        public CadError Validate(CadVersionedCommandRequest request)
        {
            if(request?.PlanVersion!=2 || request.OperationVersion!=2 || request.Command?.Command!=Name ||
                !request.Command.ManagedModelId.HasValue||request.Command.ManagedModelId==Guid.Empty||
                !request.Command.ExecutionId.HasValue||request.Command.ExecutionId==Guid.Empty||
                !request.TargetSketchEntityId.HasValue||request.TargetSketchEntityId==Guid.Empty||request.ProfileSketchEntityId.HasValue||request.Command.OutputEntityId.HasValue)
                return new CadError {Code="INVALID_EXECUTION_CONTEXT",Stage="Validate",Message="A parametric rectangle requires Host model, execution and active sketch identities."};
            var error=CadPlanningCommandContract.Validate(request.Command);
            if(error!=null)return new CadError{Code="INVALID_PARAMETERS",Stage="Validate",Message=error};
            if(request.Command.Parameters.Value<double>("centerXmm")!=0||request.Command.Parameters.Value<double>("centerYmm")!=0)
                return new CadError{Code="INVALID_PARAMETERS",Stage="Validate",Message="Parametric rectangles must be centred at the origin."};
            return null;
        }
        public Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request,CancellationToken token)=>
            _bridge.WithActiveSketchAsync(request.Command.ManagedModelId.Value,request.TargetSketchEntityId.Value,_=>null,token,request.Command.ExecutionId.Value,
                applicationAction:(app,model)=>
                {
                    var observed=ParametricRectangleBuilder.Build(app,model,request.Command.ManagedModelId.Value,request.TargetSketchEntityId.Value,
                        request.Command.Parameters.Value<double>("widthMm"),request.Command.Parameters.Value<double>("heightMm"));
                    var data=(JObject)request.Command.Parameters.DeepClone();data["parametricCapture"]=JObject.FromObject(observed);
                    return new CadCommandResult {Success=true,Data=data};
                });
    }
}
