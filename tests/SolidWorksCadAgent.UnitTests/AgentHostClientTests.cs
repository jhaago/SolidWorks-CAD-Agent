using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentHostClientTests
    {
        [TestMethod]
        public async Task HealthAndStatus_UseLocalhostApiAndParseStronglyTypedResults()
        {
            var handler = new QueueHandler(
                "{\"status\":\"ok\",\"schemaVersion\":1}",
                "{\"isConnected\":true,\"runtime\":{\"displayVersion\":\"2020 SP0.0\"}}");
            using (var client = new AgentHostClient(new HttpClient(handler)))
            {
                var health = await client.GetHealthAsync(CancellationToken.None);
                var status = await client.GetSolidWorksStatusAsync(CancellationToken.None);

                Assert.AreEqual("ok", health.Status);
                Assert.AreEqual(1, health.SchemaVersion);
                Assert.IsTrue(status.IsConnected);
                Assert.AreEqual("2020 SP0.0", status.Runtime.DisplayVersion);
                Assert.AreEqual("http://127.0.0.1:53741/health", handler.FirstUri.AbsoluteUri);
            }
        }

        [TestMethod]
        public async Task ApproveJob_SendsTheDisplayedRevisionIdentifier()
        {
            var handler = new QueueHandler("{\"id\":\"00000000-0000-0000-0000-000000000001\",\"state\":\"ReadyForReview\"}");
            using (var client = new AgentHostClient(new HttpClient(handler)))
            {
                var revision = Guid.Parse("00000000-0000-0000-0000-000000000002");
                await client.ApproveJobAsync(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    revision,
                    CancellationToken.None);

                StringAssert.Contains(handler.LastBody, revision.ToString("D"));
                StringAssert.EndsWith(handler.LastUri.AbsolutePath, "/approve");
            }
        }

        [TestMethod]
        public async Task NetworkFailure_BecomesUserReadableAgentHostUnavailableError()
        {
            using (var client = new AgentHostClient(new HttpClient(new FailingHandler())))
            {
                var error = await Assert.ThrowsExceptionAsync<AgentHostUnavailableException>(() =>
                    client.GetHealthAsync(CancellationToken.None));

                Assert.AreEqual("Agent Host unavailable. Start SolidWorksCadAgent.AgentHost and try again.", error.Message);
            }
        }

        private sealed class QueueHandler : HttpMessageHandler
        {
            private readonly string[] _responses;
            private int _index;

            public QueueHandler(params string[] responses) { _responses = responses; }
            public Uri FirstUri { get; private set; }
            public Uri LastUri { get; private set; }
            public string LastBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (FirstUri == null) FirstUri = request.RequestUri;
                LastUri = request.RequestUri;
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responses[_index++], Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new HttpRequestException("offline");
            }
        }
    }
}
