using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif
namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    internal sealed class ParametricExtrudeCommandHandler:IVersionedCadCommandHandler
    {
        private readonly SolidWorksBridgeFacade _bridge;
        internal ParametricExtrudeCommandHandler(SolidWorksBridgeFacade bridge)=>_bridge=bridge;
        public string Name=>CadCommandNames.Extrude;
        public int OperationVersion=>3;
        public CadError Validate(CadVersionedCommandRequest request)=>new VersionTwoExtrudeCommandHandler(_bridge).Validate(request);
        public Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request,CancellationToken token)=>
            _bridge.WithSelectedSketchAsync(request.Command.ManagedModelId.Value,request.ProfileSketchEntityId.Value,0,native=>
            {
#if SOLIDWORKS_INTEROP
                var model=(ModelDoc2)native;
                var result=ExtrudeCommandHandler.CreateSelectedBoss(model,request.Command.Parameters.Value<double>("depthMm"));
                if(!result.Success)return result;
                Feature owner=null;
                for(var feature=(Feature)model.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature())
                    if(feature.Name==result.Data.Value<string>("featureName")) {if(owner!=null)throw new InvalidOperationException("Ambiguous returned feature owner.");owner=feature;}
                if(owner==null)throw new InvalidOperationException("Returned feature owner missing.");
                result.Data["parametricCapture"]=JObject.FromObject(ParametricExtrudeBuilder.Capture(model,owner,request.Command.ManagedModelId.Value,
                    request.ProfileSketchEntityId.Value,request.Command.Parameters.Value<double>("depthMm")));
                return result;
#else
                return new CadCommandResult {Success=false,Error=new CadError {Code="SOLIDWORKS_INTEROP_UNAVAILABLE",Stage="Execute",Message="Native interop required."}};
#endif
            },token,request.Command.ExecutionId);
    }
}
