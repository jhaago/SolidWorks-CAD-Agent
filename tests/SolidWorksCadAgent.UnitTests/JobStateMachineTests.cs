using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Jobs;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class JobStateMachineTests
    {
        [TestMethod]
        public void HappyPath_ReachesCompleted()
        {
            var clock = new SequenceClock();
            var machine = new JobStateMachine(clock.UtcNow);
            var job = CreateJob();

            machine.Transition(job, JobState.Interpreting);
            machine.Transition(job, JobState.AwaitingApproval);
            machine.Transition(job, JobState.Approved);
            machine.Transition(job, JobState.Executing);
            machine.Transition(job, JobState.Verifying);
            machine.Transition(job, JobState.ReadyForReview);
            machine.Transition(job, JobState.Completed);

            Assert.AreEqual(JobState.Completed, job.State);
        }

        [TestMethod]
        public void ClarificationRoundTrip_ReturnsToInterpreting()
        {
            var machine = new JobStateMachine();
            var job = CreateJob();

            machine.Transition(job, JobState.Interpreting);
            machine.Transition(job, JobState.AwaitingClarification);
            machine.Transition(job, JobState.Interpreting);
            machine.Transition(job, JobState.AwaitingApproval);

            Assert.AreEqual(JobState.AwaitingApproval, job.State);
        }

        [TestMethod]
        public void Cancel_FromEveryNonTerminalState_IsAllowed()
        {
            foreach (JobState state in Enum.GetValues(typeof(JobState)))
            {
                if (state == JobState.Completed || state == JobState.Failed || state == JobState.Cancelled)
                    continue;

                var job = CreateJob();
                job.State = state;
                var machine = new JobStateMachine();

                machine.Transition(job, JobState.Cancelled);
                Assert.AreEqual(JobState.Cancelled, job.State, "Cancellation should be allowed from " + state);
            }
        }

        [TestMethod]
        public void Invalid_NewToExecuting_ThrowsWithoutChangingTimestamp()
        {
            var fixedTime = new DateTime(2026, 9, 22, 4, 0, 0, DateTimeKind.Utc);
            var job = CreateJob();
            job.UpdatedUtc = fixedTime;
            var machine = new JobStateMachine(() => fixedTime.AddHours(1));

            Assert.ThrowsException<JobStateTransitionException>(() =>
                machine.Transition(job, JobState.Executing));

            Assert.AreEqual(JobState.New, job.State);
            Assert.AreEqual(fixedTime, job.UpdatedUtc);
        }

        [TestMethod]
        public void TerminalStates_CannotResumeExecution()
        {
            foreach (var terminal in new[] { JobState.Completed, JobState.Failed, JobState.Cancelled })
            {
                var job = CreateJob();
                job.State = terminal;
                var machine = new JobStateMachine();

                Assert.ThrowsException<JobStateTransitionException>(() =>
                    machine.Transition(job, JobState.Executing),
                    terminal + " must be terminal.");
            }
        }

        [TestMethod]
        public void ValidTransition_UpdatesTimestampAfterValidation()
        {
            var first = new DateTime(2026, 9, 22, 4, 0, 0, DateTimeKind.Utc);
            var second = first.AddMinutes(5);
            var job = CreateJob();
            job.UpdatedUtc = first;
            var machine = new JobStateMachine(() => second);

            machine.Transition(job, JobState.Interpreting);

            Assert.AreEqual(JobState.Interpreting, job.State);
            Assert.AreEqual(second, job.UpdatedUtc);
        }

        private static CadJob CreateJob()
        {
            return new CadJob
            {
                Id = Guid.NewGuid(),
                Prompt = "Create a test part",
                State = JobState.New,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private sealed class SequenceClock
        {
            private int _ticks;
            public DateTime UtcNow()
            {
                return new DateTime(2026, 9, 22, 4, 0, 0, DateTimeKind.Utc).AddSeconds(_ticks++);
            }
        }
    }
}
