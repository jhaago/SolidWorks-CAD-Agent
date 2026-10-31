using System;
using System.IO;

namespace SolidWorksCadAgent.Core
{
    public enum ExecutionMode
    {
        Real = 0,
        Simulation = 1
    }

    public sealed class AgentSettings
    {
        public string WorkspaceRoot { get; set; } = @"C:\SolidWorks-CAD-Agent\Workspace";
        public bool AutoMode { get; set; } = false;
        public string HostPrefix { get; set; } = "http://127.0.0.1:53741/";
        public string OpenAiModel { get; set; } = "gpt-5.6-sol";
        public string SolidWorksExecutablePath { get; set; }
        public string LoggingLevel { get; set; } = "Information";
        public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Real;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(WorkspaceRoot))
                throw new ArgumentException("WorkspaceRoot is required.", nameof(WorkspaceRoot));
            if (!Path.IsPathRooted(WorkspaceRoot))
                throw new ArgumentException("WorkspaceRoot must be an absolute path.", nameof(WorkspaceRoot));
            if (!Uri.TryCreate(HostPrefix, UriKind.Absolute, out var hostUri) || !hostUri.IsLoopback)
                throw new ArgumentException("HostPrefix must be an absolute loopback URI.", nameof(HostPrefix));
            if (string.IsNullOrWhiteSpace(OpenAiModel))
                throw new ArgumentException("OpenAiModel is required.", nameof(OpenAiModel));
            if (!string.IsNullOrWhiteSpace(SolidWorksExecutablePath) && !Path.IsPathRooted(SolidWorksExecutablePath))
                throw new ArgumentException("SolidWorksExecutablePath must be absolute when provided.", nameof(SolidWorksExecutablePath));
            if (!IsLoggingLevel(LoggingLevel))
                throw new ArgumentException("LoggingLevel is not supported.", nameof(LoggingLevel));
            if (!Enum.IsDefined(typeof(ExecutionMode), ExecutionMode))
                throw new ArgumentException("ExecutionMode is not supported.", nameof(ExecutionMode));
        }

        private static bool IsLoggingLevel(string value)
        {
            return string.Equals(value, "Trace", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Information", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Warning", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Critical", StringComparison.OrdinalIgnoreCase);
        }
    }
}
