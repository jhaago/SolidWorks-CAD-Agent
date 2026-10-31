using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    /// <summary>COM-free design preflight. It never creates relations or claims a native solve.</summary>
    public sealed class SketchConstraintGraphCommandHandler : ICadCommandHandler
    {
        public string Name=>CadCommandNames.ValidateSketchConstraintGraph;
        public CadError Validate(JObject parameters)=>null;
        public Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var analysis=SketchConstraintGraph.Analyze(parameters);
            var result=CadCommandResult.Ok(analysis);
            if(!analysis.Valid)
            {
                result.Success=false;
                result.Error=new CadError{Code="SKETCH_GRAPH_INVALID",Stage="Validate",Message="The named sketch constraint graph has validation errors. See data.errors."};
            }
            return Task.FromResult(result);
        }
    }
}
