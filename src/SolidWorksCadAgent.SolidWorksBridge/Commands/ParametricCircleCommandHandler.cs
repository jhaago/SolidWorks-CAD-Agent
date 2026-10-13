using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    internal sealed class ParametricCircleCommandHandler:IVersionedCadCommandHandler
    {
        private readonly SolidWorksBridgeFacade bridge;
        internal ParametricCircleCommandHandler(SolidWorksBridgeFacade bridge)=>this.bridge=bridge;
        public string Name=>CadCommandNames.AddCircle;
        public int OperationVersion=>2;
        public CadError Validate(CadVersionedCommandRequest request)
        {
            if(request?.PlanVersion!=2||request.OperationVersion!=2||request.Command?.Command!=Name||
                !request.Command.ManagedModelId.HasValue||request.Command.ManagedModelId==Guid.Empty||!request.Command.ExecutionId.HasValue||request.Command.ExecutionId==Guid.Empty||
                !request.TargetSketchEntityId.HasValue||request.TargetSketchEntityId==Guid.Empty||request.ProfileSketchEntityId.HasValue||request.Command.OutputEntityId.HasValue)
                return new CadError{Code="INVALID_EXECUTION_CONTEXT",Stage="Validate",Message="A dimensioned circle requires Host model, execution and active sketch identities."};
            var error=CadPlanningCommandContract.Validate(request.Command);
            if(error!=null)return new CadError{Code="INVALID_PARAMETERS",Stage="Validate",Message=error};
            if(request.Command.Parameters.Value<double>("centerXmm")<=0.001||request.Command.Parameters.Value<double>("centerYmm")<=0.001||request.Command.Parameters.Value<double>("diameterMm")<=0.000002)
                return new CadError{Code="INVALID_PARAMETERS",Stage="Validate",Message="Dimensioned hole offsets and diameter must be positive and native-representable."};
            return null;
        }
        public Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request,CancellationToken token)=>
            bridge.WithActiveSketchAsync(request.Command.ManagedModelId.Value,request.TargetSketchEntityId.Value,_=>null,token,request.Command.ExecutionId.Value,
                applicationAction:(app,doc)=>
                {
                    var p=request.Command.Parameters;
                    var captured=ParametricHoleBuilder.Build(app,doc,request.Command.ManagedModelId.Value,request.TargetSketchEntityId.Value,p.Value<double>("diameterMm"),p.Value<double>("centerXmm"),p.Value<double>("centerYmm"));
                    var data=(JObject)p.DeepClone();data["parametricCapture"]=JObject.FromObject(captured);return new CadCommandResult{Success=true,Data=data};
                });
    }
}
