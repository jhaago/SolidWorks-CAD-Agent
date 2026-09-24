using System;
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
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        break;
                    }

                    await ProcessAsync(context, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopListener();
            _listener.Close();
        }

        private async Task ProcessAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            AgentResponse response;
            try
            {
                string body = null;
                if (context.Request.HasEntityBody)
                {
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                }

                response = await _routes.HandleAsync(
                    new AgentRequest(context.Request.HttpMethod, context.Request.RawUrl, body),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response = new AgentResponse(503, "{\"error\":{\"code\":\"HOST_STOPPING\",\"message\":\"The Agent Host is stopping.\"}}");
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
