using SolidWorksCadAgent.Contracts.Jobs;
namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed class SubmissionCreateResult
    {
        public CadJob Job { get; }
        public bool Created { get; }
        public SubmissionCreateResult(CadJob job, bool created) { Job = job; Created = created; }
    }
}
