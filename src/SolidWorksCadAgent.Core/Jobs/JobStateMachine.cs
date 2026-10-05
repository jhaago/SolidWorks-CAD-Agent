using System;
using System.Collections.Generic;
using SolidWorksCadAgent.Contracts.Jobs;

namespace SolidWorksCadAgent.Core.Jobs
{
    public sealed class JobStateTransitionException : InvalidOperationException
    {
        public JobStateTransitionException(JobState from, JobState to)
            : base(string.Format("Invalid CAD job transition from {0} to {1}.", from, to))
        {
            From = from;
            To = to;
        }

        public JobState From { get; }
        public JobState To { get; }
    }

    public sealed class JobStateMachine
    {
        private static readonly IReadOnlyDictionary<JobState, HashSet<JobState>> AllowedTransitions =
            new Dictionary<JobState, HashSet<JobState>>
            {
                [JobState.New] = new HashSet<JobState>
                {
                    JobState.Interpreting
                },
                [JobState.Interpreting] = new HashSet<JobState>
                {
                    JobState.AwaitingClarification,
                    JobState.AwaitingApproval,
                    JobState.Failed
                },
                [JobState.AwaitingClarification] = new HashSet<JobState>
                {
                    JobState.Interpreting,
                    JobState.Failed
                },
                [JobState.AwaitingApproval] = new HashSet<JobState>
                {
                    JobState.Interpreting,
                    JobState.Approved,
                    JobState.Failed
                },
                [JobState.Approved] = new HashSet<JobState>
                {
                    JobState.Executing,
                    JobState.Failed
                },
                [JobState.Executing] = new HashSet<JobState>
                {
                    JobState.Verifying,
                    JobState.Failed
                },
                [JobState.Verifying] = new HashSet<JobState>
                {
                    JobState.ReadyForReview,
                    JobState.Failed
                },
                [JobState.ReadyForReview] = new HashSet<JobState>
                {
                    JobState.Completed,
                    JobState.Failed
                },
                [JobState.Completed] = new HashSet<JobState>(),
                [JobState.Failed] = new HashSet<JobState>(),
                [JobState.Cancelled] = new HashSet<JobState>()
            };

        private readonly Func<DateTime> _utcNow;

        public JobStateMachine()
            : this(() => DateTime.UtcNow)
        {
        }

        public JobStateMachine(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public void Transition(CadJob job, JobState nextState)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var currentState = job.State;
            var isCancellation = nextState == JobState.Cancelled && !IsTerminal(currentState);
            var isAllowed = isCancellation ||
                (AllowedTransitions.TryGetValue(currentState, out var allowed) && allowed.Contains(nextState));

            if (!isAllowed)
            {
                throw new JobStateTransitionException(currentState, nextState);
            }

            job.State = nextState;
            job.UpdatedUtc = _utcNow();
        }

        public static bool IsTerminal(JobState state)
        {
            return state == JobState.Completed ||
                   state == JobState.Failed ||
                   state == JobState.Cancelled;
        }
    }
}
