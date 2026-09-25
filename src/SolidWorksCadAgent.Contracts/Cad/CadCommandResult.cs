using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

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
                Data = data == null
                    ? new JObject()
                    : JObject.FromObject(data, JsonSerializer.Create(new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    }))
            };
        }
    }
}
