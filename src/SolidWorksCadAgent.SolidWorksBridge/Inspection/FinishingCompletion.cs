using System;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    /// <summary>Final result/cleanup boundary shared by the bounded finishing ports.</summary>
    public static class FinishingCompletion
    {
        public static CadCommandResult Finish(CadCommandResult result,bool nativeInvoked,Exception cleanupError,bool cancelled,Action quarantine)
        {
            if(quarantine==null)throw new ArgumentNullException(nameof(quarantine));
            if(result==null)result=new CadCommandResult{Success=false,Error=new CadError{Code="FINISHING_RESULT_MISSING",Stage="Execute",Message="Native finishing returned no result."}};
            if(result.Data==null)result.Data=new JObject();
            if(cleanupError!=null)
            {
                result.Success=false;result.Data["selectionMayBeChanged"]=true;
                result.Error=new CadError{Code="FINISHING_SELECTION_CLEANUP_FAILED",Stage="Cleanup",Message="Finishing selection cleanup failed; inspect the part before continuing.",Detail=cleanupError.Message};
            }
            else if(nativeInvoked&&cancelled&&result.Success)
            {
                result.Success=false;
                result.Error=new CadError{Code="FINISHING_CANCELLED_AFTER_MUTATION",Stage="Execute",Message="Cancellation arrived after the native call. The part contains changes; no rollback was attempted."};
            }
            bool attempted=nativeInvoked||result.Data.Value<bool?>("mutationAttempted")==true;
            result.Data["nativeCallInvoked"]=nativeInvoked;result.Data["mutationAttempted"]=attempted;
            result.Data["documentMayBeModified"]=attempted;result.Data["rollbackAttempted"]=false;
            if(!result.Success&&attempted){quarantine();result.Data["finishingQuarantined"]=true;}
            return result;
        }
    }
}
