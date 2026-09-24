using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly AgentRoutes _routes;
        private readonly SemaphoreSlim _requestSlots = new SemaphoreSlim(16, 16);
        private readonly object _activeSync = new object();
        private readonly HashSet<Task> _activeRequests = new HashSet<Task>();
        private bool _disposed;

        public LocalHttpServer(string prefix, AgentRoutes routes)
        {
            var validatedPrefix = HostPrefixPolicy.Validate(prefix);
            _routes = routes ?? throw new ArgumentNullException(nameof(routes));
            _listener = new HttpListener();
            _listener.Prefixes.Add(validatedPrefix.AbsoluteUri);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            _listener.Start();
            using (cancellationToken.Register(StopListener))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                    {
                        _requestSlots.Release();
                        break;
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        _requestSlots.Release();
                        break;
                    }

                    var requestTask = ProcessSafelyAsync(context, cancellationToken);
                    Track(requestTask);
                }
            }

            Task[] remaining;
            lock (_activeSync)
            {
                remaining = new Task[_activeRequests.Count];
                _activeRequests.CopyTo(remaining);
            }
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopListener();
            _listener.Close();
            _requestSlots.Dispose();
        }

        private async Task ProcessSafelyAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                await ProcessAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                SafeAbort(context);
            }
            catch (HttpListenerException)
            {
                SafeAbort(context);
            }
            catch (ObjectDisposedException)
            {
                SafeAbort(context);
            }
            finally
            {
                _requestSlots.Release();
            }
        }

        private async Task ProcessAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            AgentResponse response;
            try
            {
                response = HostRequestPolicy.Validate(
                    context.Request.HttpMethod,
                    context.Request.ContentType,
                    context.Request.Headers["Origin"],
                    context.Request.ContentLength64,
                    context.Request.HasEntityBody);

                string body = null;
                if (response == null && context.Request.HasEntityBody)
                {
                    body = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
                }

                if (response == null)
                {
                    response = await _routes.HandleAsync(
                        new AgentRequest(context.Request.HttpMethod, context.Request.RawUrl, body),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response = new AgentResponse(503, "{\"error\":{\"code\":\"HOST_STOPPING\",\"message\":\"The Agent Host is stopping.\"}}");
            }
            catch (OperationCanceledException)
            {
                response = new AgentResponse(408, "{\"error\":{\"code\":\"REQUEST_TIMEOUT\",\"message\":\"The request body was not received in time.\"}}");
            }
            catch (Exception)
            {
                response = new AgentResponse(500, "{\"error\":{\"code\":\"INTERNAL_ERROR\",\"message\":\"The Agent Host could not process the request.\"}}");
            }

            var bytes = Encoding.UTF8.GetBytes(response.JsonBody ?? "{}");
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).ConfigureAwait(false);
            context.Response.Close();
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken hostCancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken))
            using (var buffer = new MemoryStream())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var chunk = new byte[8192];
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await buffer.WriteAsync(chunk, 0, read, timeout.Token).ConfigureAwait(false);
                    if (buffer.Length > HostRequestPolicy.MaximumBodyBytes)
                        throw new InvalidDataException("Request body exceeded the configured limit.");
                }

                return (request.ContentEncoding ?? Encoding.UTF8).GetString(buffer.ToArray());
            }
        }

        private void Track(Task requestTask)
        {
            lock (_activeSync)
            {
                _activeRequests.Add(requestTask);
            }

            requestTask.ContinueWith(
                completed =>
                {
                    lock (_activeSync)
                    {
                        _activeRequests.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void SafeAbort(HttpListenerContext context)
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }

        private void StopListener()
        {
            if (_listener.IsListening) _listener.Stop();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocalHttpServer));
        }
    }
}
