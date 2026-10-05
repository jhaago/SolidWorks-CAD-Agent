using System;
using System.Collections.Generic;

namespace SolidWorksCadAgent.Contracts.Jobs
{
    public sealed class CommandExecutionRecord
    {
        public Guid Id { get; set; }
        public Guid JobId { get; set; }
        public int RevisionNumber { get; set; }
        public int SequenceNumber { get; set; }
        public string CommandName { get; set; }
        public string ParametersJson { get; set; }
        public bool Success { get; set; }
        public string ResultJson { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
    }

    public sealed class VerificationResultRecord
    {
        public Guid Id { get; set; }
        public Guid JobId { get; set; }
        public int RevisionNumber { get; set; }
        public string CheckName { get; set; }
        public bool Passed { get; set; }
        public string ExpectedJson { get; set; }
        public string ActualJson { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public sealed class AttachmentRecord
    {
        public Guid Id { get; set; }
        public Guid JobId { get; set; }
        public int RevisionNumber { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public sealed class JobSnapshot
    {
        public CadJob Job { get; set; }
        public IReadOnlyList<JobRevision> Revisions { get; set; }
        public IReadOnlyList<CommandExecutionRecord> Commands { get; set; }
        public IReadOnlyList<VerificationResultRecord> Verifications { get; set; }
        public IReadOnlyList<AttachmentRecord> Attachments { get; set; }
    }
}
