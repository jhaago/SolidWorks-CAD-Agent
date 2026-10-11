using System;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class HostHealthDto
    {
        public string Status { get; set; }
        public int SchemaVersion { get; set; }
        public string ExecutionMode { get; set; }
        public string BridgeCapability { get; set; }
    }

    public sealed class SolidWorksRuntimeDto
    {
        public string RevisionNumber { get; set; }
        public int ReleaseYear { get; set; }
        public int ServicePack { get; set; }
        public int ServicePackHotfix { get; set; }
        public string DisplayVersion { get; set; }
    }

    public sealed class SolidWorksStatusDto
    {
        public bool IsConnected { get; set; }
        public bool IsRunning { get; set; }
        public bool IsVisible { get; set; }
        public SolidWorksRuntimeDto Runtime { get; set; }
        public string ErrorMessage { get; set; }
    }

    public sealed class JobPageDto
    {
        public JobViewDto[] Items { get; set; } = new JobViewDto[0];
        public string NextCursor { get; set; }
    }

    public sealed class JobViewDto
    {
        public Guid Id { get; set; }
        public string Prompt { get; set; }
        public string State { get; set; }
        public bool PlanValidated { get; set; }
        public bool HasUnresolvedAmbiguity { get; set; }
        public string AmbiguityMessage { get; set; }
        public Guid? CurrentRevisionId { get; set; }
        public int? CurrentRevisionNumber { get; set; }
        public JToken Plan { get; set; }
        public int CommandCount { get; set; }
        public JArray Verifications { get; set; }
        public JArray Commands { get; set; }
        public JArray Revisions { get; set; }
        public bool IsSimulated { get; set; }
        public string OutputPath { get; set; }
    }

    public sealed class AgentSettingsDto
    {
        public string WorkspaceRoot { get; set; }
        public bool AutoMode { get; set; }
        public string OpenAiModel { get; set; }
        public string SolidWorksExecutablePath { get; set; }
        public string LoggingLevel { get; set; }
        public string ExecutionMode { get; set; }
    }

    public sealed class CredentialStatusDto
    {
        public bool Configured { get; set; }
    }
}
