using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.RemoteAgent.Host
{
    public sealed class RemoteHttpServer : IDisposable
    {
        private readonly RemoteRoutes routes;
        private readonly HttpListener listener=new HttpListener();
        private readonly SemaphoreSlim capacity=new SemaphoreSlim(4);
        private readonly CancellationTokenSource stopping=new CancellationTokenSource();
        private Task loop;
        public RemoteHttpServer(RemoteRoutes routes) { this.routes=routes; }
        public void Start(Uri prefix) {
            if(!RemoteRequestPolicy.IsAllowedPrefix(prefix)) throw new ArgumentException("The Remote Agent must listen on loopback HTTP only.");
            if(loop!=null) throw new InvalidOperationException("Remote server already started.");
            listener.Prefixes.Add(prefix.AbsoluteUri); listener.Start(); loop=AcceptLoop();
        }
        private async Task AcceptLoop() {
            while(!stopping.IsCancellationRequested) {
                HttpListenerContext context;
                try { context=await listener.GetContextAsync().ConfigureAwait(false); } catch { break; }
                if(!capacity.Wait(0)) { context.Response.StatusCode=429; context.Response.Close(); continue; }
                _=Task.Run(async()=>{try { await Serve(context).ConfigureAwait(false); } finally {capacity.Release();}});
            }
        }
        private async Task Serve(HttpListenerContext context) {
            try {
                if(context.Request.RemoteEndPoint==null || !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address)) {context.Response.StatusCode=403;return;}
                var response=await ReadAndRoute(context).ConfigureAwait(false);
                var bytes=Encoding.UTF8.GetBytes(response.Body);
                context.Response.StatusCode=response.Status; context.Response.ContentType="application/json; charset=utf-8";
                context.Response.Headers["Cache-Control"]="no-store"; context.Response.Headers["X-Content-Type-Options"]="nosniff";
                context.Response.ContentLength64=bytes.Length;
                var write=context.Response.OutputStream.WriteAsync(bytes,0,bytes.Length);
                if(await Task.WhenAny(write,Task.Delay(5000,stopping.Token)).ConfigureAwait(false)!=write) context.Response.Abort();
                else await write.ConfigureAwait(false);
            } catch { try { context.Response.Abort(); } catch { } }
            finally { try {context.Response.Close();}catch{} }
        }
        private async Task<RemoteResponse> ReadAndRoute(HttpListenerContext context) {
            if(context.Request.ContentLength64>RemoteRequestPolicy.BodyLimit) return new RemoteResponse {Status=413,Body="{\"code\":\"request_large\",\"message\":\"The remote request is too large.\"}"};
            using(var memory=new MemoryStream()) {
                var buffer=new byte[4096]; var deadline=Task.Delay(2000,stopping.Token);
                while(true) {
                    var read=context.Request.InputStream.ReadAsync(buffer,0,buffer.Length);
                    if(await Task.WhenAny(read,deadline).ConfigureAwait(false)!=read) {context.Request.InputStream.Close();return new RemoteResponse {Status=408,Body="{\"code\":\"request_timeout\",\"message\":\"The remote request timed out.\"}"};}
                    int count=await read.ConfigureAwait(false); if(count==0) break;
                    if(memory.Length+count>RemoteRequestPolicy.BodyLimit) return new RemoteResponse {Status=413,Body="{\"code\":\"request_large\",\"message\":\"The remote request is too large.\"}"};
                    memory.Write(buffer,0,count);
                }
                return routes.Handle(new RemoteRequest {Method=context.Request.HttpMethod,Path=context.Request.RawUrl,Authorization=context.Request.Headers["Authorization"],
                    DeviceCredential=context.Request.Headers["X-Remote-Device-Credential"],ContentType=context.Request.ContentType,Body=new UTF8Encoding(false,true).GetString(memory.ToArray())});
            }
        }
        public void Dispose() { stopping.Cancel(); listener.Close(); }
    }
}
