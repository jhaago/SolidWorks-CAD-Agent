using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.SolidWorksBridge.Threading;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    public sealed class SolidWorksSession : ISolidWorksSession
    {
        private const int MkEUnavailable = unchecked((int)0x800401E3);
        private readonly SolidWorksStaDispatcher _dispatcher = new SolidWorksStaDispatcher();
        private int _disposed;

#if SOLIDWORKS_INTEROP
        private SldWorks _application;
#endif

        public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _dispatcher.InvokeAsync(AttachCore, cancellationToken);
        }

        public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _dispatcher.InvokeAsync(LaunchCore, cancellationToken);
        }

        public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _dispatcher.InvokeAsync(GetStatusCore, cancellationToken);
        }

        public Task<T> InvokeWithApplicationAsync<T>(
            Func<object, T> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ThrowIfDisposed();
            return _dispatcher.InvokeAsync(() => operation(GetApplicationCore()), cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

#if SOLIDWORKS_INTEROP
            try
            {
                _dispatcher.InvokeAsync(() =>
                {
                    ReleaseApplicationCore();
                    return 0;
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Disposal must not bring down the host if SOLIDWORKS has already exited.
            }
#endif

            _dispatcher.Dispose();
        }

#if SOLIDWORKS_INTEROP
        private SolidWorksSessionStatus AttachCore()
        {
            if (_application != null)
            {
                return BuildConnectedStatus();
            }

            try
            {
                var active = Marshal.GetActiveObject("SldWorks.Application");
                _application = active as SldWorks;

                if (_application == null)
                {
                    return BuildDisconnectedStatus("The active SOLIDWORKS COM object could not be cast to the installed interop type.");
                }

                return BuildConnectedStatus();
            }
            catch (COMException ex) when (ex.ErrorCode == MkEUnavailable)
            {
                return BuildDisconnectedStatus(null);
            }
            catch (Exception ex)
            {
                return BuildDisconnectedStatus("Unable to attach to SOLIDWORKS: " + ex.Message);
            }
        }

        private SolidWorksSessionStatus LaunchCore()
        {
            if (_application != null)
            {
                return EnsureVisibleAndBuildStatus();
            }

            try
            {
                var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
                if (type == null)
                {
                    return BuildDisconnectedStatus("SOLIDWORKS is not registered as SldWorks.Application on this machine.");
                }

                _application = Activator.CreateInstance(type) as SldWorks;
                if (_application == null)
                {
                    return BuildDisconnectedStatus("SOLIDWORKS launched but the COM object could not be cast to the installed interop type.");
                }

                return EnsureVisibleAndBuildStatus();
            }
            catch (Exception ex)
            {
                ReleaseApplicationCore();
                return BuildDisconnectedStatus("Unable to launch SOLIDWORKS: " + ex.Message);
            }
        }

        private SolidWorksSessionStatus GetStatusCore()
        {
            if (_application == null)
            {
                return BuildDisconnectedStatus(null);
            }

            try
            {
                return BuildConnectedStatus();
            }
            catch (COMException ex)
            {
                ReleaseApplicationCore();
                return BuildDisconnectedStatus("The SOLIDWORKS session is no longer available: " + ex.Message);
            }
        }

        private object GetApplicationCore()
        {
            if (_application == null)
            {
                throw new InvalidOperationException("No SOLIDWORKS application is connected. Attach or launch SOLIDWORKS first.");
            }

            return _application;
        }

        private SolidWorksSessionStatus EnsureVisibleAndBuildStatus()
        {
            _application.Visible = true;
            return BuildConnectedStatus();
        }

        private SolidWorksSessionStatus BuildConnectedStatus()
        {
            var revision = _application.RevisionNumber();
            var runtimeInfo = SolidWorksVersionParser.ParseRevision(revision);
            var compatibility = SolidWorksCompatibility.Evaluate(runtimeInfo);

            return new SolidWorksSessionStatus
            {
                IsConnected = true,
                IsRunning = true,
                IsVisible = _application.Visible,
                DispatcherThreadId = Thread.CurrentThread.ManagedThreadId,
                RuntimeInfo = runtimeInfo,
                Compatibility = compatibility,
                ErrorMessage = null
            };
        }

        private void ReleaseApplicationCore()
        {
            if (_application == null)
            {
                return;
            }

            var application = _application;
            _application = null;

            if (Marshal.IsComObject(application))
            {
                try
                {
                    Marshal.ReleaseComObject(application);
                }
                catch
                {
                    // The server may already have disconnected. The RCW will be reclaimed later.
                }
            }
        }
#else
        private SolidWorksSessionStatus AttachCore()
        {
            return BuildInteropUnavailableStatus();
        }

        private SolidWorksSessionStatus LaunchCore()
        {
            return BuildInteropUnavailableStatus();
        }

        private SolidWorksSessionStatus GetStatusCore()
        {
            return BuildInteropUnavailableStatus();
        }

        private object GetApplicationCore()
        {
            throw new PlatformNotSupportedException(
                "This build was compiled without the installed SOLIDWORKS interop libraries. Build on the SOLIDWORKS PC to enable COM access.");
        }

        private SolidWorksSessionStatus BuildInteropUnavailableStatus()
        {
            return new SolidWorksSessionStatus
            {
                IsConnected = false,
                IsRunning = false,
                IsVisible = false,
                DispatcherThreadId = Thread.CurrentThread.ManagedThreadId,
                RuntimeInfo = null,
                Compatibility = SolidWorksCompatibility.Evaluate(null),
                ErrorMessage = "SOLIDWORKS interop libraries were not available when this build was compiled."
            };
        }
#endif

        private SolidWorksSessionStatus BuildDisconnectedStatus(string errorMessage)
        {
            return new SolidWorksSessionStatus
            {
                IsConnected = false,
                IsRunning = false,
                IsVisible = false,
                DispatcherThreadId = Thread.CurrentThread.ManagedThreadId,
                RuntimeInfo = null,
                Compatibility = SolidWorksCompatibility.Evaluate(null),
                ErrorMessage = errorMessage
            };
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SolidWorksSession));
            }
        }
    }
}
