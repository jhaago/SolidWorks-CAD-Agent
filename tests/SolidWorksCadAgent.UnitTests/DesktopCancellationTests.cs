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
    public class DesktopCancellationTests
    {
        [TestMethod]
        public void Cancel_RemainsAvailableAndSendsRequestDuringPendingApproval()
        {
            DesktopWorkflowTests.OnSta(() =>
            {
                    using (var handler = new PendingApprovalHandler())
                    using (var http = new HttpClient(handler))
                    using (var client = new AgentHostClient(http))
                    using (var form = new MainForm(client))
                    {
                        Call(form, "DisplayConnection", new HostConnectionSnapshot
                        { IsAvailable = true, Health = new HostHealthDto { Status = "ok" } });
                        Call(form, "DisplayJob", new JobViewDto
                        { Id = handler.JobId, State = "AwaitingApproval", PlanValidated = true, CurrentRevisionId = Guid.NewGuid() });
                        Call(form, "ApproveButton_Click", null, EventArgs.Empty);
                        try
                        {
                            Assert.IsTrue(DesktopWorkflowTests.Field<bool>(form, "_busy"), "Approval must remain pending for this test.");
                            var cancel = (Button)typeof(MainForm).GetField("cancelButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                            Assert.IsTrue(cancel.Enabled, "Cancel must stay enabled while approval executes.");
                            Call(form, "CancelButton_Click", null, EventArgs.Empty);
                            Assert.AreEqual(1, handler.CancelRequests);
                        }
                        finally { handler.Approval.TrySetResult(Response(handler.JobId, "ReadyForReview")); }
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        while (DesktopWorkflowTests.Field<bool>(form, "_busy") && DateTime.UtcNow < deadline)
                        { Application.DoEvents(); Thread.Sleep(10); }
                        Assert.IsFalse(DesktopWorkflowTests.Field<bool>(form, "_busy"));
                        var job = (JobViewDto)typeof(MainForm).GetField("_currentJob", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                        Assert.AreEqual("Cancelled", job.State, "A late approval response must not undo cancellation.");
                    }
            });
        }

        private static object Call(MainForm form, string method, params object[] args) =>
            typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, args);

        private static HttpResponseMessage Response(Guid id, string state) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"id\":\"" + id + "\",\"state\":\"" + state + "\"}") };

        private sealed class PendingApprovalHandler : HttpMessageHandler
        {
            public readonly Guid JobId = Guid.NewGuid();
            public readonly TaskCompletionSource<HttpResponseMessage> Approval = new TaskCompletionSource<HttpResponseMessage>();
            public int CancelRequests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/approve")) return Approval.Task;
                if (request.Method == HttpMethod.Get) return Task.FromResult(Response(JobId, "Cancelled"));
                Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("/cancel"));
                CancelRequests++;
                return Task.FromResult(Response(JobId, "Cancelled"));
            }
        }
    }
}
