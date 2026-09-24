using System.Collections.Generic;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Ai
{
    public sealed class CadPlanningRequest
    {
        public string Prompt { get; set; }
        public IReadOnlyList<string> Clarifications { get; set; } = new List<string>();
    }

    public sealed class CadPlanningUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens => InputTokens + OutputTokens;
        public decimal? EstimatedCostUsd { get; set; }
    }

    public sealed class CadPlanningResult
    {
        public string Provider { get; set; }
        public string Model { get; set; }
        public string Summary { get; set; }
        public List<string> Assumptions { get; set; } = new List<string>();
        public List<string> Ambiguities { get; set; } = new List<string>();
        public List<CadCommandEnvelope> ProposedCommands { get; set; } = new List<CadCommandEnvelope>();
        public CadPlanningUsage Usage { get; set; } = new CadPlanningUsage();
    }
}
