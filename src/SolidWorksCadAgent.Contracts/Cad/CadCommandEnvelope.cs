using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadCommandEnvelope
    {
        public string Command { get; set; }
        public JObject Parameters { get; set; }
    }
}
