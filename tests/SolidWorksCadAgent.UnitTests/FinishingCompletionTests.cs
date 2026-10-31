using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FinishingCompletionTests
    {
        [TestMethod] public void SuccessfulMutationPreservesEvidenceWithoutQuarantine()
        {
            int quarantines=0;var result=FinishingCompletion.Finish(CadCommandResult.Ok(new{featureName="AgentFillet1"}),true,null,false,()=>quarantines++);
            Assert.IsTrue(result.Success);Assert.IsTrue(result.Data.Value<bool>("nativeCallInvoked"));Assert.AreEqual(0,quarantines);
        }
        [TestMethod] public void CleanupFailureAfterMutationRetainsEvidenceAndQuarantines()
        {
            int quarantines=0;var result=FinishingCompletion.Finish(CadCommandResult.Ok(new{featureName="AgentFillet1"}),true,new InvalidOperationException("cleanup"),false,()=>quarantines++);
            Assert.IsFalse(result.Success);Assert.AreEqual("FINISHING_SELECTION_CLEANUP_FAILED",result.Error.Code);
            Assert.AreEqual("AgentFillet1",result.Data.Value<string>("featureName"));Assert.IsTrue(result.Data.Value<bool>("documentMayBeModified"));
            Assert.IsFalse(result.Data.Value<bool>("rollbackAttempted"));Assert.AreEqual(1,quarantines);
        }
        [TestMethod] public void CancellationAfterMutationCannotBecomeHarmlessCancellation()
        {
            int quarantines=0;var result=FinishingCompletion.Finish(CadCommandResult.Ok(new{verified=true}),true,null,true,()=>quarantines++);
            Assert.IsFalse(result.Success);Assert.AreEqual("FINISHING_CANCELLED_AFTER_MUTATION",result.Error.Code);
            Assert.IsTrue(result.Data.Value<bool>("mutationAttempted"));Assert.AreEqual(1,quarantines);
        }
        [TestMethod] public void PreflightFailureDoesNotQuarantineAnUnmodifiedPart()
        {
            int quarantines=0;var failed=CadCommandResult.Ok(null);failed.Success=false;failed.Error=new CadError{Code="PRECHECK_FAILED"};
            var result=FinishingCompletion.Finish(failed,false,null,false,()=>quarantines++);
            Assert.IsFalse(result.Success);Assert.IsFalse(result.Data.Value<bool>("mutationAttempted"));Assert.AreEqual(0,quarantines);
        }
        [TestMethod] public void VerificationFailureAfterMutationIsQuarantinedWithoutRetry()
        {
            int quarantines=0;var failed=CadCommandResult.Ok(new{mutationAttempted=true});failed.Success=false;failed.Error=new CadError{Code="FEATURE_MATERIAL_VERIFICATION_FAILED"};
            var result=FinishingCompletion.Finish(failed,true,null,false,()=>quarantines++);
            Assert.AreEqual("FEATURE_MATERIAL_VERIFICATION_FAILED",result.Error.Code);Assert.IsTrue(result.Data.Value<bool>("finishingQuarantined"));Assert.AreEqual(1,quarantines);
        }
    }
}
