using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    /// <summary>Pure predictions. No session/COM owner, document access, native creation or solve.</summary>
    public abstract class FamilyPlanningCommandHandlerBase : ICadCommandHandler
    {
        public abstract string Name { get; }
        protected abstract JObject Plan(JObject input);
        public CadError Validate(JObject parameters)
        {try{Plan(parameters);return null;}catch(ArgumentException ex){return Invalid(ex.Message);}}
        public Task<CadCommandResult> ExecuteAsync(JObject parameters,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try{return Task.FromResult(CadCommandResult.Ok(Plan(parameters)));}
            catch(ArgumentException ex){return Task.FromResult(new CadCommandResult{Success=false,Data=new JObject(),Error=Invalid(ex.Message)});}
        }
        private static CadError Invalid(string message)=>new CadError{Code="INVALID_PARAMETERS",Stage="Plan",Message=message};
    }
    public sealed class ComponentPatternPlanCommandHandler : FamilyPlanningCommandHandlerBase
    {
        public override string Name=>CadCommandNames.PlanComponentPattern;
        protected override JObject Plan(JObject input)=>ComponentPatternPlan.Create(input);
    }
    public sealed class StandardDrawingLayoutCommandHandler : FamilyPlanningCommandHandlerBase
    {
        public override string Name=>CadCommandNames.PlanDrawingLayout;
        protected override JObject Plan(JObject input)=>StandardDrawingLayout.Create(input);
    }
}
