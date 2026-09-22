using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.SolidWorksBridge.Threading
{
    public sealed class SolidWorksStaDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private int _disposed;

        public SolidWorksStaDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SolidWorks CAD Agent STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _queue.Add(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(operation());
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, cancellationToken);
            }
            catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SolidWorksStaDispatcher));
            }

            return completion.Task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.CompleteAdding();

            if (Thread.CurrentThread.ManagedThreadId != _thread.ManagedThreadId)
            {
                _thread.Join();
            }

            _queue.Dispose();
        }

        private void Run()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SolidWorksStaDispatcher));
            }
        }
    }
}
