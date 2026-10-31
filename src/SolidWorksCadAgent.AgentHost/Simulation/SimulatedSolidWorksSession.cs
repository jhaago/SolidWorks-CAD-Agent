using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.AgentHost.Simulation
{
    public sealed class SimulatedSolidWorksSession : ISolidWorksSession
    {
        public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Status());
        }

        public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
        {
            return GetStatusAsync(cancellationToken);
        }

        public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
        {
            return GetStatusAsync(cancellationToken);
        }

        public Task<T> InvokeWithApplicationAsync<T>(
            Func<object, T> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "Simulation mode does not expose or invoke a SOLIDWORKS COM application.");
        }

        public void Dispose()
        {
        }

        private static SolidWorksSessionStatus Status()
        {
            return new SolidWorksSessionStatus
            {
                IsConnected = true,
                IsRunning = true,
                IsVisible = false,
                RuntimeInfo = new SolidWorksRuntimeInfo
                {
                    RevisionNumber = "SIMULATION",
                    DisplayVersion = "SIMULATION (no SOLIDWORKS COM)"
                },
                Compatibility = new SolidWorksCompatibilityResult
                {
                    Level = SolidWorksCompatibilityLevel.Unknown,
                    CanAttemptV1Commands = true,
                    RequiresRegressionCertification = false,
                    Message = "Deterministic simulation mode; no SOLIDWORKS runtime is attached."
                }
            };
        }
    }
}
