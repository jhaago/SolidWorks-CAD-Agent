using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public static class CadParametricPlateContract
    {
        public static bool IsParametric(CadPlanV2Document plan) => plan?.Steps?.Any(s=>s!=null &&
            ((s.Command=="AddRectangle" && s.OperationVersion==2)||(s.Command=="Extrude" && s.OperationVersion==3)||(s.Command=="AddCircle"&&s.OperationVersion==2)))==true;
        public static bool AllowsOperationVersion(string command,int version) =>
            (command=="AddRectangle" && version==2)||(command=="Extrude" && version==3)||(command=="AddCircle"&&version==2)||
            (command=="Extrude"||command=="CutExtrude" ? version==2 : CadOperationCatalog.Find(command)?.OperationVersion==version);
        public static IReadOnlyList<string> Validate(CadPlanV2Document plan)
        {
            if(CadParametricHoleContract.IsHole(plan))return CadParametricHoleContract.Validate(plan);
            var errors=new List<string>();
            var commands=new[]{"NewPart","CreateSketch","AddRectangle","ExitSketch","Extrude","SavePart"};
            var versions=new[]{1,1,2,1,3,1};
            if(plan?.PlanVersion!=2 || plan.Steps?.Count!=6 || plan.Steps.Any(s=>s?.Parameters==null))
            { errors.Add("PARAMETRIC_PLATE_SCOPE: Unsupported operationVersion outside a complete six-step Top Plane plate plan.");return errors; }
            for(int i=0;i<6;i++) if(plan.Steps[i].Command!=commands[i]||plan.Steps[i].OperationVersion!=versions[i])
                errors.Add("PARAMETRIC_PLATE_SCOPE: Unsupported step or operation version.");
            var sketch=plan.Steps[1].Parameters;var rectangle=plan.Steps[2].Parameters;var boss=plan.Steps[4].Parameters;var save=plan.Steps[5].Parameters;
            if(sketch.Value<string>("plane")!="Top Plane" || !Zero(rectangle["centerXmm"]) || !Zero(rectangle["centerYmm"]))
                errors.Add("PARAMETRIC_PLATE_FRAME: A rectangle centred on Top Plane origin is required.");
            var width=Number(rectangle["widthMm"]);var length=Number(rectangle["heightMm"]);var depth=Number(boss["depthMm"]);
            if(!Size(width)||!Size(length)||!Size(depth)||!Finite(width*length*depth)||!Finite(width*length)||width*length*depth<=0)
                errors.Add("PARAMETRIC_PLATE_SIZE: Positive native-representable finite sizes and volume are required.");
            var path=save["path"]?.Type==JTokenType.String ? save.Value<string>("path") : null;
            if(string.IsNullOrWhiteSpace(path)||Path.IsPathRooted(path)||path.Contains(":")||!path.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)||
                save["allowOverwrite"]?.Type!=JTokenType.Boolean||save.Value<bool>("allowOverwrite"))
                errors.Add("PARAMETRIC_PLATE_SAVE: An explicit new relative .sldprt path is required.");
            return errors;
        }
        public static CadParametricPlateIntent ReadIntent(CadPlanV2Document plan)
        {
            var errors=Validate(plan);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors),nameof(plan));
            if(CadParametricHoleContract.IsHole(plan))plan=CadParametricHoleContract.BasePlatePlan(plan);
            return new CadParametricPlateIntent { WidthMm=Number(plan.Steps[2].Parameters["widthMm"]),LengthMm=Number(plan.Steps[2].Parameters["heightMm"]),
                ThicknessMm=Number(plan.Steps[4].Parameters["depthMm"]),SavePath=plan.Steps[5].Parameters.Value<string>("path") };
        }
        private static double Number(JToken token) => token?.Type==JTokenType.Integer||token?.Type==JTokenType.Float ? token.Value<double>() : double.NaN;
        private static bool Zero(JToken token)=>Number(token)==0;
        private static bool Size(double value)=>Finite(value)&&value>0.000001&&Finite(value/1000)&&value/1000>0;
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
