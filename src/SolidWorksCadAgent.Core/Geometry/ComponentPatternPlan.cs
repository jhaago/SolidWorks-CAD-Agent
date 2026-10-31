// Adapted from soheil-mp/solidworks-mcp f450dab, Services/AssemblyPatternMath.cs and AssemblyMath.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: bounded net48 contracts, proper rotations, mm/metre conversion.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    /// <summary>Predicts generated instances; does not create components or a native feature.</summary>
    public static class ComponentPatternPlan
    {
        private sealed class Seed { internal string Id;internal Vec3 Origin;internal double[] Rotation; }
        public static JObject Create(JObject input)
        {
            if(input==null)throw new ArgumentException("Pattern parameters are required.");
            var pattern=PlanningInput.Text(input,"pattern");
            if(pattern=="linear")PlanningInput.Keys(input,"pattern","seeds","direction","spacingMm","count","secondDirection","secondSpacingMm","secondCount");
            else if(pattern=="circular")PlanningInput.Keys(input,"pattern","seeds","axis","axisPointMm","angleDegrees","count","equalSpacing");
            else throw new ArgumentException("pattern must be linear or circular.");
            var seeds=ReadSeeds(input["seeds"]);int count=PlanningInput.Count(input,"count",2,256);
            int secondCount=pattern=="linear"?PlanningInput.Count(input,"secondCount",1,256,1):1;
            long total=(long)seeds.Count*count*secondCount;
            if(total>256)throw new ArgumentException("Pattern exceeds the 256 total-instance budget, including seeds.");
            var placements=new JArray();double stepDegrees=0;
            if(pattern=="linear")
            {
                var first=AxisSpec.Parse(PlanningInput.Text(input,"direction"));
                double spacing=PlanningInput.Number(input,"spacingMm",1e-6,1e6);
                Vec3 second=Vec3.Zero;double secondSpacing=0;
                if(secondCount>1)
                {
                    second=AxisSpec.Parse(PlanningInput.Text(input,"secondDirection"));
                    secondSpacing=PlanningInput.Number(input,"secondSpacingMm",1e-6,1e6);
                    if(first.Cross(second).Length<=1e-9)throw new ArgumentException("Grid directions must be independent, not parallel.");
                }
                else if(input["secondDirection"]!=null||input["secondSpacingMm"]!=null)
                    throw new ArgumentException("A second direction/spacing requires secondCount greater than one.");
                foreach(var seed in seeds)for(int j=0;j<secondCount;j++)for(int i=0;i<count;i++)
                    if(i!=0||j!=0)placements.Add(Placement(seed.Id,i,j,seed.Origin+first*(i*spacing)+second*(j*secondSpacing),seed.Rotation));
            }
            else
            {
                var axis=AxisSpec.Parse(PlanningInput.Text(input,"axis"));
                var pivot=Vec3.From(PlanningInput.Array(input["axisPointMm"],"axisPointMm",3,-1e9,1e9));
                double angle=PlanningInput.Number(input,"angleDegrees",1e-6,360);
                bool equal=PlanningInput.Boolean(input,"equalSpacing",true);
                // Full circle divides n gaps; a partial span divides n-1. Never duplicate the seed at 360 degrees.
                stepDegrees=equal?(angle==360?360.0/count:angle/(count-1)):angle;
                if(!equal&&stepDegrees*(count-1)>=360-1e-9)
                    throw new ArgumentException("Explicit steps must stay below a full turn to avoid duplicate placements.");
                foreach(var seed in seeds)for(int i=1;i<count;i++)
                {
                    var rotation=Rotation(axis,i*stepDegrees*(Math.PI/180));
                    placements.Add(Placement(seed.Id,i,0,pivot+Apply(rotation,seed.Origin-pivot),Multiply(rotation,seed.Rotation)));
                }
            }
            var result=new JObject{["pattern"]=pattern,["unit"]="mm",["seedCount"]=seeds.Count,["totalInstanceCount"]=total,
                ["generatedCount"]=placements.Count,["placements"]=placements,["planningOnly"]=true,["nativeCreated"]=false,
                ["rotationConvention"]="3x3 row-major matrix acting on column vectors; solidWorksArrayData uses row vectors and metre translations"};
            if(pattern=="circular")result["stepDegrees"]=stepDegrees;
            return result;
        }
        private static List<Seed> ReadSeeds(JToken value)
        {
            var array=value as JArray;
            if(array==null||array.Count<1||array.Count>32)throw new ArgumentException("seeds must contain 1 to 32 placements.");
            var result=new List<Seed>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in array)
            {
                var seed=item as JObject??throw new ArgumentException("Each seed must be an object.");PlanningInput.Keys(seed,"id","originMm","rotation");
                string id=PlanningInput.Text(seed,"id");
                if(!Regex.IsMatch(id,@"\A[A-Za-z][A-Za-z0-9_.-]{0,127}\z")||!ids.Add(id))throw new ArgumentException("Seed IDs must be unique simple identifiers.");
                var origin=Vec3.From(PlanningInput.Array(seed["originMm"],"originMm",3,-1e9,1e9));
                var rotation=seed["rotation"]==null?new double[]{1,0,0,0,1,0,0,0,1}:PlanningInput.Array(seed["rotation"],"rotation",9,-1,1);
                ValidateRotation(rotation);result.Add(new Seed{Id=id,Origin=origin,Rotation=rotation});
            }
            return result;
        }
        private static void ValidateRotation(double[] r)
        {
            var x=Vec3.From(r);var y=Vec3.From(r,3);var z=Vec3.From(r,6);
            for(int i=0;i<3;i++)for(int j=0;j<3;j++)
            {
                double dot=0;for(int k=0;k<3;k++)dot+=r[i*3+k]*r[j*3+k];
                if(Math.Abs(dot-(i==j?1:0))>1e-9)throw new ArgumentException("Seed rotation must be orthonormal without scale/shear.");
            }
            if(Math.Abs(x.Dot(y.Cross(z))-1)>1e-9)throw new ArgumentException("Reflected seed rotations are unsupported.");
        }
        private static JObject Placement(string id,int i,int j,Vec3 origin,double[] r)
        {
            // Transpose column-vector rotation for SOLIDWORKS row-vector ArrayData. Translate mm to metres exactly once.
            var native=new double[]{r[0],r[3],r[6],r[1],r[4],r[7],r[2],r[5],r[8],origin.X/1000,origin.Y/1000,origin.Z/1000,1,0,0,0};
            return new JObject{["seedId"]=id,["indexI"]=i,["indexJ"]=j,["originMm"]=new JArray(origin.ToArray()),
                ["rotation"]=new JArray(r),["solidWorksArrayData"]=new JArray(native)};
        }
        private static double[] Rotation(Vec3 u,double radians)
        {
            double c=Math.Cos(radians),s=Math.Sin(radians),k=1-c;
            return new[]{c+u.X*u.X*k,u.X*u.Y*k-u.Z*s,u.X*u.Z*k+u.Y*s,
                u.Y*u.X*k+u.Z*s,c+u.Y*u.Y*k,u.Y*u.Z*k-u.X*s,
                u.Z*u.X*k-u.Y*s,u.Z*u.Y*k+u.X*s,c+u.Z*u.Z*k};
        }
        private static Vec3 Apply(double[] r,Vec3 p)=>new Vec3(r[0]*p.X+r[1]*p.Y+r[2]*p.Z,r[3]*p.X+r[4]*p.Y+r[5]*p.Z,r[6]*p.X+r[7]*p.Y+r[8]*p.Z);
        private static double[] Multiply(double[] a,double[] b)
        {
            var r=new double[9];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)r[i*3+j]+=a[i*3+k]*b[k*3+j];return r;
        }
    }
}
