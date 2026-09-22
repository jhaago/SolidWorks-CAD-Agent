using System;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    public sealed class SolidWorksSessionStatus
    {
        public bool IsConnected { get; set; }
        public bool IsRunning { get; set; }
        public bool IsVisible { get; set; }
        public int DispatcherThreadId { get; set; }
        public SolidWorksRuntimeInfo RuntimeInfo { get; set; }
        public SolidWorksCompatibilityResult Compatibility { get; set; }
        public string ErrorMessage { get; set; }
    }

    public interface ISolidWorksSession : IDisposable
    {
        Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken);
        Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken);
        Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken);

        // The delegate is executed on the session's dedicated STA thread. The raw COM
        // application object must never escape this callback.
        Task<T> InvokeWithApplicationAsync<T>(
            Func<object, T> operation,
            CancellationToken cancellationToken);
    }
}
