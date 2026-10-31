using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class TopologyInspectionCommandHandler : SolidWorksCommandHandlerBase
    {
        public TopologyInspectionCommandHandler(ISolidWorksSession session):base(session) { }
        public override string Name=>CadCommandNames.InspectTopology;
        public override CadError Validate(JObject parameters)
        {try{TopologyQuery.Parse(parameters);return null;}catch(ArgumentException ex){return InvalidParameter(ex.Message);}}
        public override Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)=>InvokeAsync(app=>
        {
#if SOLIDWORKS_INTEROP
            var model=RequireDocument(app) as ModelDoc2;
            if(model==null)return Failure("NO_ACTIVE_DOCUMENT","Inspect","A bound part document is required.");
            try
            {
                var query=TopologyQuery.Parse(parameters);
                bool dirty=model.GetSaveFlag();string configuration=model.ConfigurationManager.ActiveConfiguration.Name;
                var sketch=model.SketchManager.ActiveSketch;
                int selected=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1);
                token.ThrowIfCancellationRequested();
                var facts=NativeTopologyReader.Read(model,query.Kind);var matches=query.Apply(facts);
                var chain=query.OrderAsChain?TopologyQuery.OrderStraightEdges(matches):null;
                if(dirty!=model.GetSaveFlag()||configuration!=model.ConfigurationManager.ActiveConfiguration.Name||
                    !SolidWorksDocumentContext.SameIdentity(sketch,model.SketchManager.ActiveSketch)||selected!=((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1))
                    return Failure("INSPECTION_STATE_CHANGED","Inspect","Document state changed during topology inspection; results are not accepted.");
                return Ok(new{documentTitle=model.GetTitle(),configurationKey=configuration,unit="mm",kind=query.Kind,
                    observedCount=facts.Count,matchCount=matches.Count,matches,chain,inspectionOnly=true,
                    identityScope="Inspection IDs are evidence only, not persistent command selection handles.",
                    positionScope="Face positions are approximate box centers; extreme compares reported reference points, not exact face extrema."});
            }
            catch(TopologyQueryException ex){return Failure("TOPOLOGY_QUERY_FAILED","Inspect",ex.Message);}
            catch(Exception ex) when(!(ex is OperationCanceledException)){return Failure("TOPOLOGY_INSPECTION_FAILED","Inspect","Native topology inspection failed.",ex.Message);}
#else
            return InteropUnavailable();
#endif
        },token);
    }
}
