using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class EdgeFinishingInspectionCommandHandler : SolidWorksCommandHandlerBase
    {
        public EdgeFinishingInspectionCommandHandler(ISolidWorksSession session):base(session) { }
        public override string Name=>CadCommandNames.GetEdgeFinishingFeatures;
        public override CadError Validate(JObject parameters)=>parameters!=null&&parameters.Count==0?null:InvalidParameter("Finishing definition inspection accepts an empty parameter object.");
        public override Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)=>InvokeAsync(application=>
        {
#if SOLIDWORKS_INTEROP
            var model=RequireDocument(application) as ModelDoc2;
            if(model==null)return Failure("NO_ACTIVE_DOCUMENT","Inspect","A bound part document is required.");
            try
            {
                token.ThrowIfCancellationRequested();bool dirty=model.GetSaveFlag();string configuration=model.ConfigurationManager.ActiveConfiguration.Name;
                var sketch=model.SketchManager.ActiveSketch;int selected=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1);
                var features=NativeEdgeFinishingInspector.InspectDefinitions(model);RequireDocument(application);
                if(dirty!=model.GetSaveFlag()||configuration!=model.ConfigurationManager.ActiveConfiguration.Name||
                    !SolidWorksDocumentContext.SameIdentity(sketch,model.SketchManager.ActiveSketch)||selected!=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1))
                    return Failure("INSPECTION_STATE_CHANGED","Inspect","Document state changed during finishing definition inspection.");
                return Ok(new{documentTitle=model.GetTitle(),configurationKey=configuration,unit="mm",features,count=features.Length,
                    inspectionOnly=true,selectionAuthority=false});
            }
            catch(Exception ex) when(!(ex is OperationCanceledException)&&!(ex is DocumentTargetException))
            {return Failure("FINISHING_INSPECTION_FAILED","Inspect","Native finishing definition inspection failed.",ex.Message);}
#else
            return InteropUnavailable();
#endif
        },token);
    }
}
