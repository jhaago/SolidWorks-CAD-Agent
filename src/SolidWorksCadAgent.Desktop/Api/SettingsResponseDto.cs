using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class SettingsResponseDto
    {
        public JObject Settings { get; set; }
        public bool RestartRequired { get; set; }
    }
}
