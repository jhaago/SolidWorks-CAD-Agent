using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;
using SolidWorksCadAgent.Desktop.Ui;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesktopPresentationTests
    {
        [TestMethod]
        public void V2Plan_ExposesOrderedStepsAndPreventsUnsupportedRevision()
        {
            var job = new JobViewDto { State = "AwaitingApproval", PlanValidated = true,
                CurrentRevisionId = Guid.NewGuid(), CurrentRevisionNumber = 1,
                Plan = JObject.Parse("{ 'planVersion':2, 'steps':[ { 'stepKey':'new', 'command':'NewPart', 'operationVersion':2, 'parameters':{} }, { 'stepKey':'save', 'command':'SavePart', 'operationVersion':2, 'parameters':{'fileName':'part.sldprt'} } ] }") };
            var operations = JobPresentation.Operations(job).ToArray();
            Assert.AreEqual(2, operations.Length);
            Assert.AreEqual("NewPart", operations[0].Operation);
            Assert.AreEqual(2, operations[1].Sequence);
            StringAssert.Contains(operations[1].Detail, "part.sldprt");
            Assert.IsTrue(JobPresentation.CanApprove(job));
            Assert.IsFalse(JobPresentation.CanRevise(job));
        }

        [TestMethod]
        public void CompletedJob_CannotBeCancelled_AndInterpretingJobIsActive()
        {
            Assert.IsFalse(JobPresentation.CanCancel(new JobViewDto { State = "Completed" }));
            Assert.IsTrue(JobPresentation.IsActive(new JobViewDto { State = "Interpreting" }));
        }

        [TestMethod]
        public void NonAdmittedV2Plan_AllowsClarificationBeforeApproval()
        {
            var job = new JobViewDto { State = "AwaitingClarification", CurrentRevisionId = Guid.NewGuid(),
                PlanValidated = false, HasUnresolvedAmbiguity = true, Plan = JObject.Parse("{'planVersion':2,'steps':[]}") };
            Assert.IsTrue(JobPresentation.CanRevise(job), "Non-admitted plans must remain clarifiable, as allowed by the Host.");
            Assert.IsFalse(JobPresentation.CanApprove(job));
        }

        [TestMethod]
        public void PlanResults_AreScopedToDisplayedRevision()
        {
            var job = new JobViewDto { CurrentRevisionNumber = 2,
                Plan = JObject.Parse("{ 'ProposedCommands': [ { 'Command': 'Extrude', 'Parameters': { 'depthMm': 10 } } ] }"),
                Commands = JArray.Parse("[ { 'sequenceNumber':1, 'revisionNumber':1, 'success':true } ]") };
            var row = JobPresentation.Operations(job).Single();
            Assert.AreEqual("Planned", row.Status, "Earlier revision completion cannot mark this revision complete.");
            Assert.AreEqual("Extrude", row.Operation);
            StringAssert.Contains(row.Detail, "10");
            job.Commands.Add(JObject.Parse("{ 'sequenceNumber':1, 'revisionNumber':2, 'success':false }"));
            Assert.AreEqual("Failed", JobPresentation.Operations(job).Single().Status);
        }

        [TestMethod]
        public void DiagnosticRedaction_MasksNestedSecretsWithoutMutatingSnapshot()
        {
            var token = JObject.Parse("{ 'api_key':'private', 'nested': { 'Authorization':'Bearer private', 'Password':'private' }, 'message':'sk-proj-EXAMPLE1234567890', 'widthMm':100 }");
            var result = JobPresentation.SafeJson(token);
            Assert.IsFalse(result.Contains("private"));
            Assert.IsFalse(result.Contains("EXAMPLE"));
            StringAssert.Contains(result, "100");
            Assert.AreEqual("private", (string)token["api_key"]);
        }
    }
}
