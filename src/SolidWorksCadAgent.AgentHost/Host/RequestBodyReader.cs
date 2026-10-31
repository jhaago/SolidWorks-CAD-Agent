using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public static class RequestBodyReader
    {
        public static async Task<string> ReadAsync(
            Stream input,
            Encoding encoding,
            long maximumBytes,
            TimeSpan timeout,
            Action abort,
            CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (abort == null) throw new ArgumentNullException(nameof(abort));

            var stopwatch = Stopwatch.StartNew();
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = timeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        AbortQuietly(abort);
                        throw new TimeoutException("The request body was not received in time.");
                    }

                    var readTask = input.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    var deadlineTask = Task.Delay(remaining, cancellationToken);
                    var completed = await Task.WhenAny(readTask, deadlineTask).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        AbortQuietly(abort);
                        ObserveFault(readTask);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("The request body was not received in time.");
                    }

                    var read = await readTask.ConfigureAwait(false);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    if (output.Length > maximumBytes)
                        throw new InvalidDataException("Request body exceeded the configured limit.");
                }

                return encoding.GetString(output.ToArray());
            }
        }

        private static void AbortQuietly(Action abort)
        {
            try
            {
                abort();
            }
            catch
            {
            }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(
                failed =>
                {
                    if (failed.Exception != null) failed.Exception.Handle(exception => true);
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
