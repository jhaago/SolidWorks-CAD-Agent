// Adapted from soheil-mp/solidworks-mcp f450dab, Com/OleMessageFilter.cs and Win32/NativeMethods.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: net48, per-operation scope, prior filter restoration.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorksCadAgent.Core.Interop;

namespace SolidWorksCadAgent.SolidWorksBridge.Threading
{
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    internal sealed class OleMessageFilterScope : IOleMessageFilter,IDisposable
    {
        private const int RetryWindowMilliseconds=10000;
        private readonly CancellationToken _cancellation;
        private readonly int _threadId;
        private IOleMessageFilter _previous;
        private bool _disposed;
        private OleMessageFilterScope(CancellationToken cancellation)
        {
            if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("OLE filtering requires the owning STA.");
            _cancellation=cancellation;_threadId=Thread.CurrentThread.ManagedThreadId;
            int status=CoRegisterMessageFilter(this,out _previous);
            if(status!=0)throw new COMException("Unable to register the STA OLE message filter.",status);
        }
        internal static OleMessageFilterScope Register(CancellationToken cancellation)=>new OleMessageFilterScope(cancellation);
        int IOleMessageFilter.HandleInComingCall(int callType,IntPtr caller,int ticks,IntPtr interfaceInfo)=>0;
        int IOleMessageFilter.RetryRejectedCall(IntPtr callee,int ticks,int rejectionType)=>
            ComBusyRetryPolicy.DelayMilliseconds(rejectionType,ticks,RetryWindowMilliseconds,_cancellation.IsCancellationRequested);
        int IOleMessageFilter.MessagePending(IntPtr callee,int ticks,int pendingType)=>2;
        public void Dispose()
        {
            if(_disposed)return;
            if(Thread.CurrentThread.ManagedThreadId!=_threadId)throw new InvalidOperationException("Restore the OLE filter on its owning STA.");
            int status=CoRegisterMessageFilter(_previous,out var removed);
            if(status!=0)throw new COMException("Unable to restore the previous STA OLE message filter.",status);
            _disposed=true;_previous=null;
            GC.KeepAlive(this);GC.KeepAlive(removed);
        }
        [DllImport("ole32.dll",ExactSpelling=true)]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter filter,out IOleMessageFilter previous);
    }
    [ComImport,ComVisible(true),Guid("00000016-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int callType,IntPtr caller,int ticks,IntPtr interfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr callee,int ticks,int rejectionType);
        [PreserveSig] int MessagePending(IntPtr callee,int ticks,int pendingType);
    }
}
