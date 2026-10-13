using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    /// <summary>Independent Host check of complete native baseline/result observations.</summary>
    public static class CadParametricEditObservation
    {
        public static bool Matches(CadPlanV2Document expected,CadModelIdentityRecord model,JObject observed)
        {
            if(model==null||observed==null)return false;
            try {
                var geometry=CadV2PrismaticGeometry.Analyze(expected);
                var plate=observed["parametricPlate"]?.ToObject<CadParametricPlateObservation>();
                if(plate==null||plate.ModelId!=model.ModelId||plate.ConfigurationKey!=model.ConfigurationKey||plate.SketchEntityId!=expected.Steps[1].OutputEntityId||
                    CadParametricPlateVerifier.Check(CadParametricPlateContract.ReadIntent(expected),plate).Count>0)return false;
                if(CadParametricHoleContract.IsHole(expected)) {
                    var hole=observed["parametricHole"]?.ToObject<CadParametricHoleObservation>();
                    if(hole==null||hole.ModelId!=model.ModelId||hole.ConfigurationKey!=model.ConfigurationKey||hole.SketchEntityId!=expected.Steps[5].OutputEntityId||
                        CadParametricHoleVerifier.Check(CadParametricHoleContract.ReadIntent(expected),hole).Count>0)return false;
                }else if(observed["parametricHole"]!=null&&observed["parametricHole"].Type!=JTokenType.Null)return false;
                if(observed["hasRebuildErrors"]?.Type!=JTokenType.Boolean||observed.Value<bool>("hasRebuildErrors")||
                    observed["bodies"]?["bodyCount"]?.Type!=JTokenType.Integer||observed["bodies"].Value<int>("bodyCount")!=1)return false;
                var volume=Number(observed["bodies"]?["volumeMm3"]);
                if(!Close(volume,geometry.VolumeMm3,Math.Max(0.05,geometry.VolumeMm3*0.0001)))return false;
                if(geometry.ExpectedBounds().Properties().Any(p=>!Close(Number(observed["bounds"]?[p.Name]),p.Value.Value<double>(),0.02)))return false;
                return observed["nativeCuts"] is JArray cuts&&CadV2NativeCutReadbackMatcher.Matches(geometry.Cuts,cuts,out _);
            }catch(Exception ex)when(ex is ArgumentException||ex is JsonException||ex is InvalidOperationException||ex is FormatException||ex is OverflowException){return false;}
        }
        private static double Number(JToken token)=>token?.Type==JTokenType.Integer||token?.Type==JTokenType.Float?token.Value<double>():double.NaN;
        private static bool Close(double actual,double expected,double tolerance)=>!double.IsNaN(actual)&&!double.IsInfinity(actual)&&Math.Abs(actual-expected)<=tolerance;
    }
}
