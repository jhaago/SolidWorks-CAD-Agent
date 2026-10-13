using System;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public enum CadParametricEditAttemptStatus {Prepared,Checkpointed,MutationStarted,Applied,Saving,Saved,Restoring,Restored,Published,Uncertain}
    public sealed class CadParametricEditAttemptRecord
    {
        public Guid Id {get;set;}
        public Guid JobId {get;set;}
        public Guid RevisionId {get;set;}
        public Guid ModelId {get;set;}
        public Guid CheckpointModelRevisionId {get;set;}
        public Guid ModelRevisionId {get;set;}
        public string PlanSha256 {get;set;}
        public string WorkingPath {get;set;}
        public string CheckpointPath {get;set;}
        public string CheckpointSha256 {get;set;}
        public string OutputPath {get;set;}
        public string SavedSha256 {get;set;}
        public string FailureReason {get;set;}
        public CadParametricEditAttemptStatus Status {get;set;}
    }
}
