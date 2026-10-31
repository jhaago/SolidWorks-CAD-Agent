using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadParametricEditSource
    {
        [JsonProperty("jobId", Required=Required.Always)] public Guid JobId { get; set; }
        [JsonProperty("revisionId", Required=Required.Always)] public Guid RevisionId { get; set; }
        [JsonProperty("modelId", Required=Required.Always)] public Guid ModelId { get; set; }
        [JsonProperty("modelRevisionId", Required=Required.Always)] public Guid ModelRevisionId { get; set; }
        [JsonProperty("configurationKey", Required=Required.Always)] public string ConfigurationKey { get; set; }
        [JsonProperty("path", Required=Required.Always)] public string Path { get; set; }
        [JsonProperty("sha256", Required=Required.Always)] public string Sha256 { get; set; }
        [JsonProperty("planSha256", Required=Required.Always)] public string PlanSha256 { get; set; }
        [JsonProperty("sourcePlanJson", Required=Required.Always)] public string SourcePlanJson { get; set; }
        [JsonExtensionData] public IDictionary<string,JToken> AdditionalProperties { get; set; }
    }

    /// <summary>An edit is an exact source-bound operation, never a creation-plan replay.</summary>
    public static class CadParametricEditContract
    {
        public const string ForkCommand="ForkParametricPart";
        public const string SetCommand="SetDrivingParameter";
        private static readonly string[] Roles={"width","length","thickness","holeDiameter","holeX","holeY"};
        public static bool IsEdit(CadPlanV2Document plan)=>plan?.ParametricEdit!=null;

        public static CadPlanV2Document Build(CadParametricEditSource source,string parameter,double valueMm,string outputPath)
        {
            var plan=new CadPlanV2Document {
                PlanVersion=2, ParametricEdit=source,
                Summary=string.Format(CultureInfo.InvariantCulture,"Edit a copy of verified job {0}: set {1} to {2} mm. Preserve the source; verify regeneration and saved output, or restore the checkpoint.",source?.JobId,parameter,valueMm),
                Assumptions=new List<string>{"One driving dimension; existing source features and all other parameters retained.","A fresh child identity and a non-overwriting output path are required."},
                Ambiguities=new List<string>(), Steps=new List<CadPlanV2Step> {
                    Step("fork",ForkCommand,new JObject()),
                    Step("parameter",SetCommand,new JObject{{"parameter",parameter},{"valueMm",valueMm}}),
                    Step("save","SavePart",new JObject{{"path",outputPath},{"allowOverwrite",false}})
                }
            };
            var errors=Validate(plan);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors));
            return plan;
        }
        public static IReadOnlyList<string> Validate(CadPlanV2Document plan)
        {
            var errors=new List<string>();
            if(plan?.PlanVersion!=2||plan.ParametricEdit==null||plan.Steps?.Count!=3||plan.Steps.Any(s=>s?.Parameters==null))
                return new[]{"PARAMETRIC_EDIT_SCOPE: An exact source-bound three-step edit is required."};
            if(plan.AdditionalProperties?.Count>0||plan.ParametricEdit.AdditionalProperties?.Count>0)
                errors.Add("PARAMETRIC_EDIT_SCOPE: Unexpected metadata.");
            var commands=new[]{ForkCommand,SetCommand,"SavePart"};var keys=new[]{"fork","parameter","save"};
            for(var i=0;i<3;i++) {
                var s=plan.Steps[i];
                if(s.Command!=commands[i]||s.StepKey!=keys[i]||s.OperationVersion!=1||s.Inputs!=null||s.OutputEntityId.HasValue||s.OutputKey!=null||s.AdditionalProperties?.Count>0)
                    errors.Add("PARAMETRIC_EDIT_SCOPE: Unexpected operation, reference or output.");
            }
            if(plan.Steps[0].Parameters.Count!=0||!Fields(plan.Steps[1].Parameters,"parameter","valueMm")||!Fields(plan.Steps[2].Parameters,"path","allowOverwrite"))
                errors.Add("PARAMETRIC_EDIT_SCOPE: Unexpected operation parameters.");
            var source=plan.ParametricEdit;
            if(source.JobId==Guid.Empty||source.RevisionId==Guid.Empty||source.ModelId==Guid.Empty||source.ModelRevisionId==Guid.Empty||string.IsNullOrWhiteSpace(source.ConfigurationKey)||!RelativePart(source.Path)||!Hash(source.Sha256)||!Hash(source.PlanSha256))
                errors.Add("PARAMETRIC_EDIT_SOURCE: Complete immutable source identity and SHA256 values are required.");
            if(string.IsNullOrWhiteSpace(source.SourcePlanJson)||source.SourcePlanJson.Length>100000||!string.Equals(HashText(source.SourcePlanJson??""),source.PlanSha256,StringComparison.Ordinal))
                errors.Add("PARAMETRIC_EDIT_SOURCE: The source plan SHA256 does not match.");
            var save=plan.Steps[2].Parameters;
            if(save["path"]?.Type!=JTokenType.String||!RelativePart(save.Value<string>("path"))||save["allowOverwrite"]?.Type!=JTokenType.Boolean||save.Value<bool>("allowOverwrite")||string.Equals(save.Value<string>("path"),source.Path,StringComparison.OrdinalIgnoreCase))
                errors.Add("PARAMETRIC_EDIT_SAVE: A different new relative .sldprt path with overwrite false is required.");
            var change=plan.Steps[1].Parameters;
            var role=change["parameter"]?.Type==JTokenType.String?change.Value<string>("parameter"):null;
            var value=change["valueMm"]?.Type==JTokenType.Float||change["valueMm"]?.Type==JTokenType.Integer?change.Value<double>("valueMm"):double.NaN;
            if(!Roles.Contains(role)||double.IsNaN(value)||double.IsInfinity(value)||value<=0)
                errors.Add("PARAMETRIC_EDIT_PARAMETER: One supported finite positive driving parameter is required.");
            if(errors.Count>0)return errors;
            try {
                var original=ReadSource(source);
                if(!CadParametricHoleContract.IsHole(original)&&role.StartsWith("hole",StringComparison.Ordinal))
                    throw new ArgumentException("A hole parameter requires a verified hole source.");
                var expected=Derive(plan,original);
                errors.AddRange(CadPlanV2Preflight.Validate(expected,true));
                CadV2PrismaticGeometry.Analyze(expected);
                var old=Parameter(original,role);
                if(old.Value<double>()==value) errors.Add("PARAMETRIC_EDIT_PARAMETER: The requested parameter is unchanged.");
            }catch(ArgumentException ex){errors.Add("PARAMETRIC_EDIT_SOURCE: "+ex.Message);}
            return errors;
        }
        public static CadPlanV2Document ExpectedPart(CadPlanV2Document plan)
        {
            if(!IsEdit(plan))return plan;
            var errors=Validate(plan);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors));
            return Derive(plan,ReadSource(plan.ParametricEdit));
        }
        public static CadPlanV2Document SourcePart(CadPlanV2Document plan)=>ReadSource(plan.ParametricEdit);
        public static string HashText(string json)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-","").ToLowerInvariant();
        }
        public static bool RelativePart(string path)=>!string.IsNullOrWhiteSpace(path)&&!System.IO.Path.IsPathRooted(path)&&!path.Contains(":")&&path.EndsWith(".sldprt",StringComparison.OrdinalIgnoreCase)&&
            !path.Split('/','\\').Any(segment=>segment==".."||segment=="."||segment.Length==0);
        private static bool Hash(string value)=>value?.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        private static bool Fields(JObject value,params string[] names)=>value.Count==names.Length&&value.Properties().All(p=>names.Contains(p.Name));
        private static CadPlanV2Document ReadSource(CadParametricEditSource source)
        {
            JObject root;
            try {root=JObject.Parse(source.SourcePlanJson,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});}
            catch(JsonException ex){throw new ArgumentException("Malformed source creation plan.",ex);}
            // Check before recursive parsing: nested edits can never recurse into this validator.
            if(root.Properties().Any(p=>string.Equals(p.Name,"parametricEdit",StringComparison.OrdinalIgnoreCase)&&p.Value.Type!=JTokenType.Null))
                throw new ArgumentException("Chained edits are outside the first capability slice.");
            var parsed=CadPlanDocumentReader.ReadPersisted(source.SourcePlanJson);
            if(!parsed.IsValid||parsed.Version!=2||!CadParametricPlateContract.IsParametric(parsed.CandidateV2))
                throw new ArgumentException("The source must be a normalized parametric creation plan.");
            return parsed.CandidateV2;
        }
        private static CadPlanV2Document Derive(CadPlanV2Document edit,CadPlanV2Document original)
        {
            original=JObject.FromObject(original).ToObject<CadPlanV2Document>();
            var role=edit.Steps[1].Parameters.Value<string>("parameter");
            var field=Field(role);var index=Index(role);
            original.Steps[index].Parameters[field]=edit.Steps[1].Parameters["valueMm"].DeepClone();
            original.Steps.Last().StepKey="save";
            original.Steps.Last().Parameters=(JObject)edit.Steps[2].Parameters.DeepClone();
            return original;
        }
        private static JToken Parameter(CadPlanV2Document part,string role)=>part.Steps[Index(role)].Parameters[Field(role)];
        private static int Index(string role)=>role=="width"||role=="length"?2:role=="thickness"?4:6;
        private static string Field(string role)=>role=="width"?"widthMm":role=="length"?"heightMm":role=="thickness"?"depthMm":role=="holeDiameter"?"diameterMm":role=="holeX"?"centerXmm":"centerYmm";
        private static CadPlanV2Step Step(string key,string command,JObject parameters)=>new CadPlanV2Step{StepKey=key,Command=command,OperationVersion=1,Parameters=parameters};
    }
}
