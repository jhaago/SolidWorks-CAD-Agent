using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Desktop;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesktopHistoryTests
    {
        [TestMethod]
        public void History_LoadsPagesWithoutDuplicatesAndOpensFullSnapshot()
        {
            RunSta(() =>
            {
                using (var handler = new HistoryHandler())
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    ((Task)Call(form, "LoadHistoryAsync", false)).GetAwaiter().GetResult();
                    var list = (ListBox)Field(form, "historyListBox");
                    Assert.AreEqual(1, list.Items.Count);
                    Assert.AreEqual(0, handler.SnapshotRequests, "Binding summaries must not load a snapshot automatically.");
                    ((Task)Call(form, "LoadHistoryAsync", true)).GetAwaiter().GetResult();
                    Assert.AreEqual(2, list.Items.Count, "Overlapping pages must be deduplicated.");
                    Call(form, "DisplayConnection", new HostConnectionSnapshot { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                    Call(form, "DisplayJob", new JobViewDto { Id = handler.Id, State = "Completed" });
                    // Explicit selection loads the full persisted plan rather than trusting a summary.
                    ((Task)Call(form, "OpenHistoryJobAsync", handler.OtherId)).GetAwaiter().GetResult();
                    var job = (JobViewDto)Field(form, "_currentJob");
                    Assert.AreEqual(handler.OtherId, job.Id);
                    Assert.IsTrue(job.PlanValidated);
                    Assert.IsNotNull(job.CurrentRevisionId);
                    Assert.AreEqual(1, handler.SnapshotRequests);
                }
            });
        }

        [TestMethod]
        public void History_CannotSwitchCancellationTargetWhileExecuting()
        {
            RunSta(() =>
            {
                using (var handler = new HistoryHandler())
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    Call(form, "DisplayConnection", new HostConnectionSnapshot { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                    Call(form, "DisplayJob", new JobViewDto { Id = handler.Id, State = "Executing" });
                    ((Task)Call(form, "OpenHistoryJobAsync", handler.OtherId)).GetAwaiter().GetResult();
                    Assert.AreEqual(handler.Id, ((JobViewDto)Field(form, "_currentJob")).Id);
                    Assert.AreEqual(0, handler.SnapshotRequests);
                    Assert.IsFalse(((ListBox)Field(form, "historyListBox")).Enabled);
                    Assert.IsTrue(((Button)Field(form, "cancelButton")).Enabled);
                }
            });
        }

        [TestMethod]
        public void HealthyConnection_RetriesFailedHistoryWithoutReconnect()
        {
            RunSta(() =>
            {
                using (var handler = new HistoryHandler { FailNextPage = true })
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    Call(form, "DisplayConnection", new HostConnectionSnapshot { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                    ((Task)Call(form, "RefreshDesktopStateAsync")).GetAwaiter().GetResult();
                    Assert.IsFalse((bool)Field(form, "_historyLoaded"));
                    ((Task)Call(form, "RefreshDesktopStateAsync")).GetAwaiter().GetResult();
                    Assert.IsTrue((bool)Field(form, "_historyLoaded"));
                    Assert.AreEqual(1, ((ListBox)Field(form, "historyListBox")).Items.Count);
                }
            });
        }

        [TestMethod]
        public void RecoveredExecutingJob_RefreshesTerminalStateAndUnlocksHistory()
        {
            RunSta(() =>
            {
                using (var handler = new HistoryHandler { SnapshotState = "Executing" })
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    Call(form, "DisplayConnection", new HostConnectionSnapshot { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                    ((Task)Call(form, "OpenHistoryJobAsync", handler.OtherId)).GetAwaiter().GetResult();
                    Assert.IsFalse(((ListBox)Field(form, "historyListBox")).Enabled);
                    handler.SnapshotState = "Completed";
                    ((Task)Call(form, "RefreshDesktopStateAsync")).GetAwaiter().GetResult();
                    Assert.AreEqual("Completed", ((JobViewDto)Field(form, "_currentJob")).State);
                    Assert.IsTrue(((ListBox)Field(form, "historyListBox")).Enabled);
                    Assert.AreEqual(2, handler.SnapshotRequests);
                }
            });
        }
        [TestMethod]
        public void RecoveredExecutingJob_BlocksNewSubmissionAndPreservesCancelTarget()
        {
            RunSta(() =>
            {
                using (var handler = new HistoryHandler())
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    Call(form, "DisplayConnection", new HostConnectionSnapshot { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                    Call(form, "DisplayJob", new JobViewDto { Id = handler.Id, State = "Executing" });
                    Assert.IsFalse(((Button)Field(form, "sendButton")).Enabled);
                    ((TextBox)Field(form, "promptTextBox")).Text = "another plate";
                    Call(form, "SendButton_Click", null, EventArgs.Empty);
                    Assert.AreEqual(0, handler.SnapshotRequests);
                    Assert.AreEqual(handler.Id, ((JobViewDto)Field(form, "_currentJob")).Id);
                    Assert.AreEqual("another plate", ((TextBox)Field(form, "promptTextBox")).Text);
                }
            });
        }
        private static object Call(MainForm form, string method, params object[] args) =>
            typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, args);
        private static object Field(MainForm form, string name) =>
            typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var previous = WindowsFormsSynchronizationContext.AutoInstall;
                try { WindowsFormsSynchronizationContext.AutoInstall = false; action(); }
                catch (Exception ex) { failure = ex; }
                finally { WindowsFormsSynchronizationContext.AutoInstall = previous; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure != null) throw failure;
        }
        private sealed class HistoryHandler : HttpMessageHandler
        {
            public readonly Guid Id = Guid.NewGuid();
            public readonly Guid OtherId = Guid.NewGuid();
            public int SnapshotRequests;
            public bool FailNextPage;
            public string SnapshotState = "AwaitingApproval";
            private int _pages;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string body;
                if (request.RequestUri.AbsolutePath == "/jobs")
                {
                    if (FailNextPage) { FailNextPage = false; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{} ") }); }
                    _pages++;
                    body = "{\"items\":[{\"id\":\"" + Id + "\",\"prompt\":\"plate\",\"state\":\"Completed\"}" +
                        (_pages == 1 ? "" : ",{\"id\":\"" + OtherId + "\",\"prompt\":\"bracket\",\"state\":\"AwaitingApproval\"}") +
                        "],\"nextCursor\":" + (_pages == 1 ? "\"next\"" : "null") + "}";
                }
                else
                {
                    SnapshotRequests++;
                    body = "{\"id\":\"" + OtherId + "\",\"state\":\"" + SnapshotState + "\",\"planValidated\":true,\"currentRevisionId\":\"" + Guid.NewGuid() + "\"}";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }
    }
}