using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadCommandEnvelope
    {
        // Assigned by the Host, never accepted from model or HTTP command JSON.
        [Newtonsoft.Json.JsonIgnore]
        public Guid? ExecutionId { get; set; }

        // Trusted Host execution context only. These are never supplied by planners or serialized plans.
        [JsonIgnore]
        public Guid? ManagedModelId { get; set; }

        [JsonIgnore]
        public Guid? OutputEntityId { get; set; }
        public string Command { get; set; }
        public JObject Parameters { get; set; }
    }
}
