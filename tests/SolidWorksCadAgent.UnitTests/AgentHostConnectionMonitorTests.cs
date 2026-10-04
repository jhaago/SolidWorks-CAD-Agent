using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentHostConnectionMonitorTests
    {
        [TestMethod]
        public async Task HostStartsLate_RetriesWithBackoffAndPublishesRecoveredStatus()
        {
            using (var stop = new CancellationTokenSource())
            using (var http = new HttpClient(new StartupHandler()))
            using (var client = new AgentHostClient(http))
            {
                var delays = new List<TimeSpan>();
                var updates = new List<HostConnectionSnapshot>();
                var monitor = new AgentHostConnectionMonitor(client, (delay, token) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });
                await monitor.RunAsync(snapshot =>
                {
                    updates.Add(snapshot);
                    if (snapshot.IsAvailable) stop.Cancel();
                }, stop.Token);
                CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
                Assert.IsFalse(updates[0].IsAvailable);
                Assert.IsTrue(updates[updates.Count - 1].IsAvailable);
                Assert.AreEqual("ok", updates[updates.Count - 1].Health.Status);
                Assert.IsTrue(updates[updates.Count - 1].SolidWorks.IsConnected);
            }
        }

        [TestMethod]
        public async Task OfflineBackoff_IsCappedAndCancellationStopsFurtherProbes()
        {
            using (var stop = new CancellationTokenSource())
            using (var http = new HttpClient(new StartupHandler(int.MaxValue)))
            using (var client = new AgentHostClient(http))
            {
                var delays = new List<TimeSpan>();
                var monitor = new AgentHostConnectionMonitor(client, (delay, token) =>
                {
                    delays.Add(delay);
                    if (delays.Count == 7) stop.Cancel();
                    return Task.CompletedTask;
                });
                await monitor.RunAsync(snapshot => { }, stop.Token);
                CollectionAssert.AreEqual(new[] { 2, 4, 8, 16, 30, 30, 30 },
                    delays.ConvertAll(delay => (int)delay.TotalSeconds));
            }
        }

        [TestMethod]
        public async Task AvailableHost_UsesThirtySecondChecksThenRecoversAfterRestart()
        {
            using (var stop = new CancellationTokenSource())
            using (var http = new HttpClient(new RestartHandler()))
            using (var client = new AgentHostClient(http))
            {
                var delays = new List<int>();
                var updates = new List<bool>();
                var monitor = new AgentHostConnectionMonitor(client, (delay, token) =>
                {
                    delays.Add((int)delay.TotalSeconds);
                    return Task.CompletedTask;
                });
                await monitor.RunAsync(snapshot =>
                {
                    updates.Add(snapshot.IsAvailable);
                    if (updates.Count == 3) stop.Cancel();
                }, stop.Token);
                CollectionAssert.AreEqual(new[] { true, false, true }, updates);
                CollectionAssert.AreEqual(new[] { 30, 2 }, delays);
            }
        }

        [TestMethod]
        public async Task CompleteJob_SendsSinglePostWithoutRetryingAnUnavailableHost()
        {
            var handler = new CountingFailureHandler();
            using (var http = new HttpClient(handler))
            using (var client = new AgentHostClient(http))
            {
                await Assert.ThrowsExceptionAsync<AgentHostUnavailableException>(() =>
                    client.CompleteJobAsync(Guid.NewGuid(), CancellationToken.None));
                Assert.AreEqual(1, handler.Count);
                Assert.AreEqual(HttpMethod.Post, handler.Method);
                StringAssert.EndsWith(handler.Path, "/complete");
            }
        }

        [TestMethod]
        public async Task SolidWorksProbeFailure_DoesNotMarkHealthyHostUnavailable()
        {
            using (var stop = new CancellationTokenSource())
            using (var http = new HttpClient(new StatusFailureHandler()))
            using (var client = new AgentHostClient(http))
            {
                HostConnectionSnapshot observed = null;
                var monitor = new AgentHostConnectionMonitor(client);
                await monitor.RunAsync(snapshot => { observed = snapshot; stop.Cancel(); }, stop.Token);
                Assert.IsTrue(observed.IsAvailable, "A busy or unavailable SOLIDWORKS status probe must not hide a healthy Host.");
                Assert.AreEqual("ok", observed.Health.Status);
                Assert.IsNull(observed.SolidWorks);
            }
        }

        private sealed class StatusFailureHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath != "/health") throw new HttpRequestException("SOLIDWORKS status delayed");
                return Task.FromResult(Reply(request));
            }
        }

        private class StartupHandler : HttpMessageHandler
        {
            private int _failures;
            public StartupHandler(int failures = 2) { _failures = failures; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_failures-- > 0) throw new HttpRequestException("Host not started");
                return Task.FromResult(Reply(request));
            }
        }

        private sealed class RestartHandler : HttpMessageHandler
        {
            private int _healthCalls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath == "/health" && ++_healthCalls == 2)
                    throw new HttpRequestException("Host restarting");
                return Task.FromResult(Reply(request));
            }
        }

        private static HttpResponseMessage Reply(HttpRequestMessage request) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri.AbsolutePath == "/health"
                ? "{\"status\":\"ok\",\"schemaVersion\":1}"
                : "{\"isConnected\":true}")
        };

        private sealed class CountingFailureHandler : HttpMessageHandler
        {
            public int Count { get; private set; }
            public string Path { get; private set; }
            public HttpMethod Method { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++; Path = request.RequestUri.AbsolutePath; Method = request.Method;
                throw new HttpRequestException("offline");
            }
        }
    }
}
