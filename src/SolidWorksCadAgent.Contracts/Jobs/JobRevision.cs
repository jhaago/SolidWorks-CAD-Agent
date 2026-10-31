using System;

namespace SolidWorksCadAgent.Contracts.Jobs
{
    public sealed class JobRevision
    {
        public Guid Id { get; set; }
        public Guid JobId { get; set; }
        public int RevisionNumber { get; set; }
        public string Prompt { get; set; }
        public string InterpretationJson { get; set; }
        public string PlanJson { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
