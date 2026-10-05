using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("SolidWorksCadAgent.UnitTests")]

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    internal static class SolidWorksNormalLaunch
    {
        internal static async Task<SolidWorksSessionStatus> RunAsync(
            Func<Task<SolidWorksSessionStatus>> attach, Func<Task<bool>> ready,
            Action start, Func<CancellationToken, Task> wait, int attempts, CancellationToken token,
            TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested();
            var status = new SolidWorksSessionStatus();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
                try
                {
                    status = await WaitForProbeAsync(attach(), deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!status.IsConnected) start();
                    for (var attempt = 0; attempt < attempts; attempt++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (status.IsConnected && await WaitForProbeAsync(ready(), deadline.Token).ConfigureAwait(false)) return status;
                        await wait(deadline.Token).ConfigureAwait(false);
                        status = await WaitForProbeAsync(attach(), deadline.Token).ConfigureAwait(false);
                    }
                    if (status.IsConnected && await WaitForProbeAsync(ready(), deadline.Token).ConfigureAwait(false)) return status;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            return new SolidWorksSessionStatus
            {
                IsConnected = false, IsRunning = status.IsRunning, IsVisible = status.IsVisible,
                DispatcherThreadId = status.DispatcherThreadId,
                ErrorMessage = "SOLIDWORKS launch timed out waiting for COM registration and startup. " +
                    "Check the SOLIDWORKS window for startup or licence dialogs, then try Attach. " + status.ErrorMessage
            };
        }

        private static async Task<T> WaitForProbeAsync<T>(Task<T> probe, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(probe, cancelled.Task).ConfigureAwait(false) != probe)
                {
                    // An in-flight COM call stays on its STA; never abort the thread or replay it.
                    // Observe a later fault after the caller has stopped waiting.
                    _ = probe.ContinueWith(task => { var ignored = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                return await probe.ConfigureAwait(false);
            }
        }

        internal static string ParseExecutable(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("SOLIDWORKS executable is not registered.");
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var closingQuote = command.IndexOf('"', 1);
                if (closingQuote < 0) throw new InvalidOperationException("SOLIDWORKS executable registration is invalid.");
                return command.Substring(1, closingQuote - 1);
            }
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw new InvalidOperationException("SOLIDWORKS executable registration is invalid.");
            return command.Substring(0, end + 4);
        }

        internal static string ResolveExecutable(string executableOverride)
        {
            string path = executableOverride;
            if (string.IsNullOrWhiteSpace(path))
            {
                using (var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64))
                using (var progId = classes.OpenSubKey(@"SldWorks.Application\CLSID"))
                {
                    var clsid = progId?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(clsid)) throw new InvalidOperationException("SOLIDWORKS is not registered on this machine.");
                    using (var server = classes.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32"))
                        path = ParseExecutable(server?.GetValue(null) as string);
                }
            }
            path = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("SOLIDWORKS executable path is invalid or missing: " + path);
            return path;
        }
    }
}
