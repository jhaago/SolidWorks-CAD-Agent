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
        Task<object> GetApplicationAsync(CancellationToken cancellationToken);
    }
}
