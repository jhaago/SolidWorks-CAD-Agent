using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class FaceBoundaryInspectionCommandHandler : SolidWorksCommandHandlerBase
    {
        public FaceBoundaryInspectionCommandHandler(ISolidWorksSession session):base(session) { }
        public override string Name=>CadCommandNames.InspectFaceBoundary;
        public override CadError Validate(JObject parameters)
        {var error=FaceFeatureValidation.ValidateFaceSketch(parameters);return error==null?null:InvalidParameter(error);}
        public override Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)=>InvokeAsync(app=>
        {
#if SOLIDWORKS_INTEROP
            var model=RequireDocument(app) as ModelDoc2;
            if(model==null)return Failure("NO_ACTIVE_DOCUMENT","Inspect","A bound part document is required.");
            try
            {
                bool dirty=model.GetSaveFlag();string configuration=model.ConfigurationManager.ActiveConfiguration.Name;
                var sketch=model.SketchManager.ActiveSketch;int selected=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1);
                var data=NativeFaceSelector.Inspect(model,(string)parameters["normalAxis"],(string)parameters["side"],token);
                RequireDocument(app); // The active document must still be the same bound document.
                if(dirty!=model.GetSaveFlag()||configuration!=model.ConfigurationManager.ActiveConfiguration.Name||
                    !SolidWorksDocumentContext.SameIdentity(sketch,model.SketchManager.ActiveSketch)||selected!=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1))
                    return Failure("INSPECTION_STATE_CHANGED","Inspect","Document state changed during face-boundary inspection; evidence is not accepted.");
                var result=Ok(data);result.Data["configurationKey"]=configuration;result.Data["documentTitle"]=model.GetTitle();return result;
            }
            catch(TopologyQueryException ex){return Failure("FACE_BOUNDARY_QUERY_FAILED","Inspect",ex.Message);}
            catch(Exception ex) when(!(ex is OperationCanceledException)&&!(ex is DocumentTargetException))
            {return Failure("FACE_BOUNDARY_INSPECTION_FAILED","Inspect","Native face-boundary inspection failed.",ex.Message);}
#else
            return InteropUnavailable();
#endif
        },token);
    }
}
