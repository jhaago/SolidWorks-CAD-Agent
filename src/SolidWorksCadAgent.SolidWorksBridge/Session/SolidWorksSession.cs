using System;
using System.Diagnostics;
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
        private readonly string _executablePath;
        private readonly SemaphoreSlim _launchGate = new SemaphoreSlim(1, 1);

        public SolidWorksSession() : this(null) { }

        public SolidWorksSession(string executablePath)
        {
            _executablePath = executablePath;
        }

#if SOLIDWORKS_INTEROP
        private SldWorks _application;
#endif

        public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _dispatcher.InvokeAsync(AttachCore, cancellationToken);
        }

        public async Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
#if SOLIDWORKS_INTEROP
            await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var result = await SolidWorksNormalLaunch.RunAsync(
                    () => AttachAsync(cancellationToken),
                    () => _dispatcher.InvokeAsync(() => _application.StartupProcessCompleted, cancellationToken),
                    () =>
                    {
                        var path = SolidWorksNormalLaunch.ResolveExecutable(_executablePath);
                        using (var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, Arguments = "" }))
                        {
                            if (process == null) throw new InvalidOperationException("SOLIDWORKS process could not be started.");
                        }
                    },
                    token => Task.Delay(500, token), 120, cancellationToken).ConfigureAwait(false);
                return result.IsConnected
                    ? await _dispatcher.InvokeAsync(EnsureVisibleAndBuildStatus, cancellationToken).ConfigureAwait(false)
                    : result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return await _dispatcher.InvokeAsync(() => BuildDisconnectedStatus("Unable to launch SOLIDWORKS: " + ex.Message), cancellationToken).ConfigureAwait(false);
            }
            finally { _launchGate.Release(); }
#else
            return await _dispatcher.InvokeAsync(LaunchCore, cancellationToken).ConfigureAwait(false);
#endif
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
                ActiveDocumentTitle = ReadActiveDocumentTitle(),
                ErrorMessage = null
            };
        }

        private string ReadActiveDocumentTitle()
        {
            ModelDoc2 document = null;
            try
            {
                document = _application.IActiveDoc2;
                return document?.GetTitle();
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                if (document != null && Marshal.IsComObject(document))
                {
                    try { Marshal.ReleaseComObject(document); }
                    catch { }
                }
            }
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
                ActiveDocumentTitle = null,
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
                ActiveDocumentTitle = null,
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
