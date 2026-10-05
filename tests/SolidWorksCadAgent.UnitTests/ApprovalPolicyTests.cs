using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Jobs;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ApprovalPolicyTests
    {
        [TestMethod]
        public void ApprovalMode_AwaitingApproval_CannotExecute()
        {
            var job = ReadyPlan(JobState.AwaitingApproval);
            var settings = new AgentSettings { AutoMode = false };

            Assert.IsFalse(ApprovalPolicy.CanExecute(job, settings));
        }

        [TestMethod]
        public void ApprovalMode_ApprovedJob_CanExecute()
        {
            var job = ReadyPlan(JobState.Approved);
            var settings = new AgentSettings { AutoMode = false };

            Assert.IsTrue(ApprovalPolicy.CanExecute(job, settings));
        }

        [TestMethod]
        public void AutoMode_UnambiguousValidatedPlan_CanExecuteWithoutManualApproval()
        {
            var job = ReadyPlan(JobState.AwaitingApproval);
            var settings = new AgentSettings { AutoMode = true };

            Assert.IsTrue(ApprovalPolicy.CanExecute(job, settings));
        }

        [TestMethod]
        public void AutoMode_UnresolvedAmbiguity_CannotExecute()
        {
            var job = ReadyPlan(JobState.AwaitingApproval);
            job.HasUnresolvedAmbiguity = true;
            var settings = new AgentSettings { AutoMode = true };

            Assert.IsFalse(ApprovalPolicy.CanExecute(job, settings));
        }

        [TestMethod]
        public void AnyMode_OverwriteRequestedWithoutExplicitAuthorization_CannotExecute()
        {
            foreach (var autoMode in new[] { false, true })
            {
                var job = ReadyPlan(autoMode ? JobState.AwaitingApproval : JobState.Approved);
                job.OverwriteRequested = true;
                job.OverwriteAuthorized = false;
                var settings = new AgentSettings { AutoMode = autoMode };

                Assert.IsFalse(ApprovalPolicy.CanExecute(job, settings));
            }
        }

        [TestMethod]
        public void ApprovedOverwrite_WithExplicitAuthorization_CanExecute()
        {
            var job = ReadyPlan(JobState.Approved);
            job.OverwriteRequested = true;
            job.OverwriteAuthorized = true;

            Assert.IsTrue(ApprovalPolicy.CanExecute(job, new AgentSettings { AutoMode = false }));
        }

        [TestMethod]
        public void InvalidPlan_CannotExecuteEvenWhenApproved()
        {
            var job = ReadyPlan(JobState.Approved);
            job.PlanValidated = false;

            Assert.IsFalse(ApprovalPolicy.CanExecute(job, new AgentSettings { AutoMode = false }));
        }

        private static CadJob ReadyPlan(JobState state)
        {
            return new CadJob
            {
                Id = Guid.NewGuid(),
                Prompt = "Create a test part",
                State = state,
                PlanValidated = true,
                HasUnresolvedAmbiguity = false,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }
    }
}
