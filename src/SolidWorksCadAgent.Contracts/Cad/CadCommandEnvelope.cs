using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadCommandEnvelope
    {
        // Assigned by the Host, never accepted from model or HTTP command JSON.
        [Newtonsoft.Json.JsonIgnore]
        public System.Guid? ExecutionId { get; set; }
        public string Command { get; set; }
        public JObject Parameters { get; set; }
    }
}
