namespace SolidWorksCadAgent.Core
{
    public sealed class AgentSettings
    {
        public string WorkspaceRoot { get; set; } = @"C:\SolidWorks-CAD-Agent\Workspace";
        public bool AutoMode { get; set; } = false;
        public string HostPrefix { get; set; } = "http://127.0.0.1:53741/";
        public string OpenAiModel { get; set; } = "gpt-5.6-sol";
    }
}
