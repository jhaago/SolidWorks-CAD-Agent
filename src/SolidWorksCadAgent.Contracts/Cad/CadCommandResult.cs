using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadCommandResult
    {
        public bool Success { get; set; }
        public JObject Data { get; set; }
        public CadError Error { get; set; }

        public static CadCommandResult Ok(object data)
        {
            return new CadCommandResult
            {
                Success = true,
                Data = data == null ? new JObject() : JObject.FromObject(data)
            };
        }
    }
}
