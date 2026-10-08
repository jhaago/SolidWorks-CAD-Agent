using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadPlanV2Document
    {
        [JsonProperty("planVersion", Required = Required.Always)] public int? PlanVersion { get; set; }
        [JsonProperty("summary")] public string Summary { get; set; }
        [JsonProperty("assumptions")] public List<string> Assumptions { get; set; }
        [JsonProperty("ambiguities")] public List<string> Ambiguities { get; set; }
        [JsonProperty("steps", Required = Required.Always)] public List<CadPlanV2Step> Steps { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> AdditionalProperties { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadPlanV2Step
    {
        [JsonProperty("stepKey", Required = Required.Always)] public string StepKey { get; set; }
        [JsonProperty("command", Required = Required.Always)] public string Command { get; set; }
        [JsonProperty("operationVersion", Required = Required.Always)] public int? OperationVersion { get; set; }
        [JsonProperty("parameters", Required = Required.Always)] public JObject Parameters { get; set; }
        [JsonProperty("outputKey")] public string OutputKey { get; set; }
        [JsonProperty("outputEntityId")] public Guid? OutputEntityId { get; set; }
        [JsonProperty("inputs")] public CadPlanV2Inputs Inputs { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> AdditionalProperties { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadPlanV2Inputs
    {
        [JsonProperty("profileSketch")] public CadPlanV2Reference ProfileSketch { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> AdditionalProperties { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadPlanV2Reference
    {
        [JsonProperty("kind", Required = Required.Always)] public string Kind { get; set; }
        [JsonProperty("outputKey", Required = Required.Always)] public string OutputKey { get; set; }
        [JsonProperty("entityId")] public Guid? EntityId { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> AdditionalProperties { get; set; }
    }
}
