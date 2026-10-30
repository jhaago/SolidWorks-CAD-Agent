using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    internal static class PlanningInput
    {
        internal static void Keys(JObject input,params string[] allowed)
        {
            if(input==null)throw new ArgumentException("Planning parameters are required.");
            var unknown=input.Properties().FirstOrDefault(p=>!allowed.Contains(p.Name));
            if(unknown!=null)throw new ArgumentException("Unknown planning parameter: "+unknown.Name+".");
        }
        internal static string Text(JObject input,string name,string fallback=null)
        {
            if(input[name]==null&&fallback!=null)return fallback;
            if(input[name]?.Type!=JTokenType.String||string.IsNullOrWhiteSpace((string)input[name])||((string)input[name]).Length>128)
                throw new ArgumentException(name+" must be a nonempty string of at most 128 characters.");
            return (string)input[name];
        }
        internal static double Number(JObject input,string name,double min,double max,double? fallback=null)
        {
            if(input[name]==null&&fallback.HasValue)return fallback.Value;
            return Number(input[name],name,min,max);
        }
        internal static double Number(JToken value,string name,double min,double max)
        {
            if(value==null||value.Type!=JTokenType.Float&&value.Type!=JTokenType.Integer)
                throw new ArgumentException(name+" must be numeric.");
            double number;
            try{number=value.Value<double>();}catch(Exception ex) when(ex is OverflowException||ex is FormatException)
            {throw new ArgumentException(name+" is outside the supported numeric range.",ex);}
            if(!Vec3.Finite(number)||number<min||number>max)
                throw new ArgumentException(name+" is outside the supported finite range.");
            return number;
        }
        internal static int Count(JObject input,string name,int min,int max,int? fallback=null)
        {
            if(input[name]==null&&fallback.HasValue)return fallback.Value;
            if(input[name]?.Type!=JTokenType.Integer||!int.TryParse(input[name].ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var count)||count<min||count>max)
                throw new ArgumentException(name+" must be an integer between "+min+" and "+max+".");
            return count;
        }
        internal static bool Boolean(JObject input,string name,bool fallback)
        {
            if(input[name]==null)return fallback;
            if(input[name].Type!=JTokenType.Boolean)throw new ArgumentException(name+" must be boolean.");
            return input.Value<bool>(name);
        }
        internal static double[] Array(JToken value,string name,int count,double min,double max)
        {
            var array=value as JArray;
            if(array==null||array.Count!=count)throw new ArgumentException(name+" must contain exactly "+count+" numeric values.");
            return array.Select(v=>Number(v,name,min,max)).ToArray();
        }
    }
}
