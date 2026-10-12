using System;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public enum CadV2MutationAttemptStatus { Prepared, Applied, Uncertain }

    /// <summary>Durable provenance only; no status here authorizes CAD dispatch.</summary>
    public sealed class CadV2MutationAttemptRecord
    {
        internal CadV2MutationAttemptRecord(Guid id, Guid jobId, Guid revisionId, string planSha256,
            string stepKey, Guid modelId, Guid? outputEntityId, Guid prospectiveModelRevisionId,
            CadV2MutationAttemptStatus status)
        {
            Id = id;
            JobId = jobId;
            RevisionId = revisionId;
            PlanSha256 = planSha256;
            StepKey = stepKey;
            ModelId = modelId;
            OutputEntityId = outputEntityId;
            ProspectiveModelRevisionId = prospectiveModelRevisionId;
            Status = status;
        }

        public Guid Id { get; }
        public Guid JobId { get; }
        public Guid RevisionId { get; }
        public string PlanSha256 { get; }
        public string StepKey { get; }
        public Guid ModelId { get; }
        public Guid? OutputEntityId { get; }
        public Guid ProspectiveModelRevisionId { get; }
        public CadV2MutationAttemptStatus Status { get; }
    }
}
