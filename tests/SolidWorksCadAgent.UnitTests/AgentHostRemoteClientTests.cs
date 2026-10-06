using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.RemoteAgent.Host;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentHostRemoteClientTests
    {
        [TestMethod]
        public void Status_BlockedCadDispatcherStillReturnsWithinPhoneDeadline()
        {
            using (var client = new AgentHostRemoteClient(new BusyCadHandler()))
            {
                var timer = Stopwatch.StartNew();
                var response = client.Forward("GET", "status", null);
                Assert.AreEqual(200, response.Status);
                Assert.IsTrue(timer.ElapsedMilliseconds < 1500, "Status must leave room within Android's two-second deadline.");
            }
        }

        private sealed class BusyCadHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var path = request.RequestUri.AbsolutePath;
                if (path == "/solidworks/status") await Task.Delay(10000, token);
                var body = path == "/settings" ? "{\"settings\":{\"executionMode\":\"Real\"}}" :
                    path == "/jobs" ? "{\"items\":[]}" : "{}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }

        [TestMethod]
        public void Status_UnattachedSolidWorksWithNullRuntimeRemainsAvailable()
        {
            using (var client = new AgentHostRemoteClient(new UnattachedHandler()))
                Assert.AreEqual(200, client.Forward("GET", "status", null).Status);
        }

        private sealed class UnattachedHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(request.RequestUri.AbsolutePath == "/solidworks/status" ? "{\"runtime\":null}" : "{}") });
        }
    }
}
