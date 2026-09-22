using System;

namespace SolidWorksCadAgent.Contracts.Jobs
{
    public sealed class CreateJobRequest
    {
        public string Prompt { get; set; }
    }

    public sealed class JobDto
    {
        public Guid Id { get; set; }
        public string Prompt { get; set; }
        public JobState State { get; set; }
        public bool PlanValidated { get; set; }
        public bool HasUnresolvedAmbiguity { get; set; }
        public string AmbiguityMessage { get; set; }
        public bool OverwriteRequested { get; set; }
        public bool OverwriteAuthorized { get; set; }
        public string OutputPath { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
