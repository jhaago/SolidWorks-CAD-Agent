using System;
using SolidWorksCadAgent.Contracts.Jobs;

namespace SolidWorksCadAgent.Core.Jobs
{
    public static class ApprovalPolicy
    {
        public static bool CanExecute(CadJob job, AgentSettings settings)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (!job.PlanValidated || job.HasUnresolvedAmbiguity)
            {
                return false;
            }

            if (job.OverwriteRequested && !job.OverwriteAuthorized)
            {
                return false;
            }

            if (job.State == JobState.Approved)
            {
                return true;
            }

            return settings.AutoMode && job.State == JobState.AwaitingApproval;
        }
    }
}
