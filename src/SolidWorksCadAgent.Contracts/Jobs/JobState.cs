namespace SolidWorksCadAgent.Contracts.Jobs
{
    public enum JobState
    {
        New,
        Interpreting,
        AwaitingClarification,
        AwaitingApproval,
        Approved,
        Executing,
        Verifying,
        ReadyForReview,
        Completed,
        Failed,
        Cancelled
    }
}
