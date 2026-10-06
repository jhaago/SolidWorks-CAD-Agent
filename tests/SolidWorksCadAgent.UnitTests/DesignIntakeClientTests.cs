using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesignIntakeClientTests
    {
        [TestMethod]
        public async Task ApprovalSendsExactRevisionAndNeverCreatesCadPlan()
        {
            var handler = new Handler();
            using (var client = new DesignIntakeClient(new HttpClient(handler)))
            {
                var id = Guid.NewGuid();
                var revision = Guid.NewGuid();
                await client.ApproveAsync(id, revision, CancellationToken.None);
                Assert.AreEqual("http://127.0.0.1:53741/designs/" + id + "/approve", handler.Uri.AbsoluteUri);
                Assert.AreEqual(revision.ToString("D"), (string)JObject.Parse(handler.Body)["revisionId"]);
                Assert.AreEqual(1, handler.Count);
            }
        }
        [TestMethod]
        public async Task AttachmentDoesNotSendAnalysisMessageAndIncludesMetadata()
        {
            var handler = new Handler();
            using (var client = new DesignIntakeClient(new HttpClient(handler)))
            {
                await client.AddReferenceAsync(Guid.NewGuid(), "plate.png", new byte[] {
 1, 2, 3 }
, "Front", "front", CancellationToken.None);
                var body = JObject.Parse(handler.Body);
                Assert.AreEqual("AQID", (string)body["base64"]);
                Assert.AreEqual("Front", (string)body["label"]);
                StringAssert.EndsWith(handler.Uri.AbsolutePath, "/references");
                Assert.AreEqual(1, handler.Count);
            }
        }
        [TestMethod]
        public async Task OversizedAttachmentIsRejectedBeforeHttp()
        {
            var handler = new Handler();
            using (var client = new DesignIntakeClient(new HttpClient(handler)))
            {
                await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.AddReferenceAsync(Guid.NewGuid(), "a.png", new byte[4 * 1024 * 1024 + 1], null, null, CancellationToken.None));
                Assert.AreEqual(0, handler.Count);
            }
        }
        [TestMethod]
        public async Task ServerErrorsDoNotExposeResponseContent()
        {
            var handler = new Handler
            {
                Code = HttpStatusCode.BadRequest,
                Json = "{\"error\":\"secret host path\"}"
            };
            using (var client = new DesignIntakeClient(new HttpClient(handler)))
            {
                var error = await Assert.ThrowsExceptionAsync<AgentHostApiException>(() => client.CreateAsync("Plate", CancellationToken.None));
                Assert.IsFalse(error.Message.Contains("secret"));
            }
        }
        [TestMethod]
        public void DesktopPlanButtonCreatesCadJobInsteadOfBeingBlockedByItsBusyGuard()
        {
            RunSta(() =>
            {
            var id = Guid.NewGuid(); var revision = Guid.NewGuid(); var job = Guid.NewGuid(); Guid? loadedJob = null;
            var session = new JObject { ["Id"] = id.ToString(), ["Title"] = "Plate", ["State"] = "DesignApproved", ["ApprovedRevisionId"] = revision.ToString(), ["Revisions"] = new JArray(new JObject { ["Id"] = revision.ToString(), ["Number"] = 1, ["Brief"] = new JObject { ["UnsupportedFeatures"] = new JArray() } }) };
            var returned = (JObject)session.DeepClone(); returned["CadJobId"] = job.ToString();
            var handler = new Handler { Json = returned.ToString() };
            using (var client = new DesignIntakeClient(new HttpClient(handler)))
            using (var form = new SolidWorksCadAgent.Desktop.DesignIntakeForm(client, created => loadedJob = created))
            {
                var type = form.GetType(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                type.GetMethod("Display", flags).Invoke(form, new object[] { session });
                var button = (System.Windows.Forms.Button)type.GetField("_plan", flags).GetValue(form);
                Assert.IsTrue(button.Enabled);
                typeof(System.Windows.Forms.Button).GetMethod("OnClick", flags).Invoke(button, new object[] { EventArgs.Empty });
                var deadline = DateTime.UtcNow.AddSeconds(2); while (!loadedJob.HasValue && DateTime.UtcNow < deadline) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
                Assert.IsTrue(loadedJob.HasValue, "The plan button must send its request and deliver the job callback. HTTP requests: " + handler.Count);
                Assert.AreEqual(job, loadedJob.Value); Assert.AreEqual(1, handler.Count);
                StringAssert.EndsWith(handler.Uri.AbsolutePath, "/plan-cad");
                Assert.AreEqual(revision.ToString(), (string)JObject.Parse(handler.Body)["revisionId"]);
                Assert.IsFalse(button.Enabled, "An existing CAD job must be edited through job revisions.");
            }
                    });
        }
        private static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                var autoInstall = System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall;
                try { System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = false; action(); }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = autoInstall; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Desktop action test timed out.");
            if (error != null) throw error;
        }
        private sealed class Handler : HttpMessageHandler
        {
            public Uri Uri;
            public string Body;
            public int Count;
            public HttpStatusCode Code = HttpStatusCode.OK;
            public string Json = "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"State\":\"ImageReceived\"}";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                Uri = request.RequestUri;
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(Code)
                {
                    Content = new StringContent(Json)
                };
            }
        }
    }
}

