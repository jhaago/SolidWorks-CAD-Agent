using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public static class CadParametricHoleContract
    {
        public static bool IsHole(CadPlanV2Document plan)=>plan?.Steps?.Any(s=>s?.Command=="AddCircle"&&s.OperationVersion==2)==true;
        public static CadPlanV2Document BasePlatePlan(CadPlanV2Document plan)=>new CadPlanV2Document{
            PlanVersion=plan.PlanVersion,Summary=plan.Summary,Steps=plan.Steps.Take(5).Concat(new[]{plan.Steps[9]}).ToList()};
        public static IReadOnlyList<string> Validate(CadPlanV2Document plan)
        {
            var errors=new List<string>();
            if(plan?.PlanVersion!=2||plan.Steps?.Count!=10||plan.Steps.Any(s=>s?.Parameters==null))
            {errors.Add("PARAMETRIC_HOLE_SCOPE: Unsupported operationVersion outside a complete ten-step Top Plane plate and hole plan.");return errors;}
            var commands=new[]{"NewPart","CreateSketch","AddRectangle","ExitSketch","Extrude","CreateSketch","AddCircle","ExitSketch","CutExtrude","SavePart"};
            var versions=new[]{1,1,2,1,3,1,2,1,2,1};
            for(int i=0;i<10;i++)if(plan.Steps[i].Command!=commands[i]||plan.Steps[i].OperationVersion!=versions[i])errors.Add("PARAMETRIC_HOLE_SCOPE: Unsupported step or operationVersion.");
            // Stop before projection when a new-version circle has been substituted into the base prefix.
            if(errors.Count>0)return errors;
            errors.AddRange(CadParametricPlateContract.Validate(BasePlatePlan(plan)));
            if(plan.Steps[5].Parameters.Value<string>("plane")!="Top Plane"||plan.Steps[8].Parameters.Value<string>("endCondition")!="ThroughAll")
                errors.Add("PARAMETRIC_HOLE_FRAME: A second Top Plane sketch and through-all cut are required.");
            var p=plan.Steps[6].Parameters;double x=Number(p["centerXmm"]),y=Number(p["centerYmm"]),r=Number(p["diameterMm"])/2;
            double width=Number(plan.Steps[2].Parameters["widthMm"]),length=Number(plan.Steps[2].Parameters["heightMm"]);
            if(!Finite(x)||!Finite(y)||!Finite(r)||x<=0.001||y<=0.001||r<=0.000001||!Finite(r*2)||
                !Finite(x+r)||!Finite(y+r)||x+r>width/2-0.001||y+r>length/2-0.001)
                errors.Add("PARAMETRIC_HOLE_SIZE: Positive offsets and a finite circle fully inside the plate with0.001mm clearance are required.");
            var path=plan.Steps[9].Parameters.Value<string>("path");
            if(path!=null&&path.Split(new[]{'/','\\'}).Any(segment=>segment==".."||segment=="."))errors.Add("PARAMETRIC_HOLE_SAVE: Path traversal is unsupported.");
            return errors;
        }
        public static CadParametricHoleIntent ReadIntent(CadPlanV2Document plan)
        {
            var errors=Validate(plan);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors),nameof(plan));
            var p=plan.Steps[6].Parameters;return new CadParametricHoleIntent{DiameterMm=Number(p["diameterMm"]),Xmm=Number(p["centerXmm"]),Ymm=Number(p["centerYmm"])};
        }
        private static double Number(JToken t)=>t?.Type==JTokenType.Integer||t?.Type==JTokenType.Float?t.Value<double>():double.NaN;
        private static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
    }
}

