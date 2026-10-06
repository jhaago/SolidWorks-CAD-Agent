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

        [TestMethod]
        public void Lifecycle_ForwardsValidatedRevisionPayloadAndArtifactRead()
        {
            using (var client = new AgentHostRemoteClient(new UnattachedHandler()))
            {
                var id = Guid.NewGuid().ToString("D");
                foreach (var action in new[] { "approve", "request-changes", "complete" })
                    Assert.AreEqual(200, client.Forward("POST", "jobs/" + id + "/" + action, "{}").Status);
                Assert.AreEqual(200, client.Forward("GET", "jobs/" + id + "/artifact", null).Status);
                Assert.AreEqual(404, client.Forward("POST", "jobs/" + id + "/delete", "{}").Status);
            }
        }

        [TestMethod]
        public void Artifact_UsesLongerBoundedTransferDeadlineThanStatus()
        {
            using (var client = new AgentHostRemoteClient(new SlowArtifactHandler()))
                Assert.AreEqual(200, client.Forward("GET", "jobs/" + Guid.NewGuid() + "/artifact", null).Status);
        }

        private sealed class SlowArtifactHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                await Task.Delay(1000, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
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
