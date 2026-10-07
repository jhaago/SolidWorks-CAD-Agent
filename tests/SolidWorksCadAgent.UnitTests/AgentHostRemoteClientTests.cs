using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
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
        public void Status_ActiveClarificationIncludesTheCurrentRevisionAndQuestion()
        {
            var id = Guid.NewGuid();
            var revision = Guid.NewGuid();
            using (var client = new AgentHostRemoteClient(new ClarificationHandler(id, revision)))
            {
                var response = client.Forward("GET", "status", null);
                Assert.AreEqual(200, response.Status);
                Assert.IsTrue((bool)JObject.Parse(response.Body)["jobInputImages"]);
                var active = (JObject)JObject.Parse(response.Body)["activeJob"];
                Assert.AreEqual("AwaitingClarification", (string)active["state"]);
                Assert.AreEqual(revision.ToString("D"), (string)active["currentRevisionId"]);
                Assert.AreEqual("Which diameter?", (string)active["plan"]["Ambiguities"][0]);
                Assert.IsNull(active["revisions"], "Workstation status should include only the current revision's display fields.");
            }
        }

        [TestMethod]
        public void Status_DetailUnavailableRetainsTheBriefJobAndWorkstationState()
        {
            var id = Guid.NewGuid();
            using (var client = new AgentHostRemoteClient(new ClarificationHandler(id, Guid.NewGuid(), detailAvailable: false)))
            {
                var response = client.Forward("GET", "status", null);
                Assert.AreEqual(200, response.Status);
                var active = (JObject)JObject.Parse(response.Body)["activeJob"];
                Assert.AreEqual(id.ToString("D"), (string)active["id"]);
                Assert.IsNull(active["currentRevisionId"]);
            }
        }

        private sealed class ClarificationHandler : HttpMessageHandler
        {
            private readonly Guid id;
            private readonly Guid revision;
            private readonly bool detailAvailable;

            public ClarificationHandler(Guid id, Guid revision, bool detailAvailable = true)
            { this.id = id; this.revision = revision; this.detailAvailable = detailAvailable; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var path = request.RequestUri.AbsolutePath;
                if (path == "/jobs/" + id && !detailAvailable)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                var body = path == "/jobs" ? "{\"items\":[{\"id\":\"" + id + "\",\"prompt\":\"Make a plate\",\"state\":\"AwaitingClarification\"}]}" :
                    path == "/jobs/" + id ? "{\"id\":\"" + id + "\",\"prompt\":\"Make a plate\",\"state\":\"AwaitingClarification\",\"currentRevisionId\":\"" + revision + "\",\"currentRevisionNumber\":1,\"plan\":{\"Ambiguities\":[\"Which diameter?\"]},\"revisions\":[{\"prompt\":\"old work\"}]}" :
                    path == "/health" ? "{\"jobInputImages\":true}" :
                    path == "/settings" ? "{\"settings\":{}}" :
                    path == "/solidworks/status" ? "{\"runtime\":null}" : "{}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
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
