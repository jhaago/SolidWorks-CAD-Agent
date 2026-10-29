// Adapted from soheil-mp/solidworks-mcp f450dab, Com/OleMessageFilter.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: pure bounded policy, cancellation, invalid tick rejection.
namespace SolidWorksCadAgent.Core.Interop
{
    public static class ComBusyRetryPolicy
    {
        public static int DelayMilliseconds(int rejectionType,int elapsedMilliseconds,int retryWindowMilliseconds,bool cancelled)
        {
            // Only SERVERCALL_RETRYLATER is a refusal before execution, not an accepted mutation failure.
            if(rejectionType!=2||elapsedMilliseconds<0||retryWindowMilliseconds<=0||elapsedMilliseconds>=retryWindowMilliseconds||cancelled)return -1;
            return 150;
        }
    }
}
