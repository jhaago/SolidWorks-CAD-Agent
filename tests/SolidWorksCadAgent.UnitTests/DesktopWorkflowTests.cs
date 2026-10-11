using System;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Desktop;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesktopWorkflowTests
    {
        private static readonly Lazy<StaHost> Host = new Lazy<StaHost>(() => new StaHost());

        [TestMethod]
        public void ModernWorkspace_RetainsDesignIntakeAndCompletionActions()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new MainForm(client))
                {
                    var buttons = Descendants(form).OfType<Button>().ToArray();
                    Assert.IsTrue(buttons.Any(button => button.Text.Contains("Design intake")), "Existing image/design intake must remain reachable.");
                    Invoke(form, "DisplayJob", new JobViewDto { Id = Guid.NewGuid(), State = "ReadyForReview" });
                    Assert.IsTrue(buttons.Any(button => button.Text.Contains("Complete") && button.Enabled), "A verified job must still expose explicit completion.");
                }
            });
        }

        [TestMethod]
        public void ReviewTabs_ExposeSelectedStateToAccessibilityClients()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new PreviewForm(client))
                {
                    PrepareForRender(form);
                    var buttons = Descendants(form).OfType<Button>().Where(button => button.AccessibleRole == AccessibleRole.PageTab).ToArray();
                    var plan = buttons.Single(button => button.Text == "Plan");
                    var activity = buttons.Single(button => button.Text == "Activity");
                    Assert.IsTrue((plan.AccessibilityObject.State & AccessibleStates.Selected) != 0);
                    activity.PerformClick();
                    Assert.IsTrue((activity.AccessibilityObject.State & AccessibleStates.Selected) != 0);
                    Assert.IsFalse((plan.AccessibilityObject.State & AccessibleStates.Selected) != 0);
                }
            });
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        [TestMethod]
        public void HistoryItemText_RedactsKeysForNativeAccessibilityAsWellAsPainting()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new MainForm(client))
                {
                    Invoke(form, "DisplayJob", new JobViewDto { Id = Guid.NewGuid(), Prompt = "Part sk-proj-EXAMPLE1234567890", State = "Failed" });
                    var history = Field<ListBox>(form, "historyListBox");
                    Assert.IsFalse(history.GetItemText(history.Items[0]).Contains("EXAMPLE"));
                }
            });
        }

        [TestMethod]
        public void CancelledJobPolling_ReconcilesLateInFlightOperationResults()
        {
            OnSta(() =>
            {
                var id = Guid.NewGuid();
                var snapshot = new JobViewDto { Id = id, State = "Cancelled",
                    Commands = Newtonsoft.Json.Linq.JArray.Parse("[{ 'sequenceNumber':1, 'revisionNumber':1, 'commandName':'Extrude', 'success':true }]") };
                using (var http = new HttpClient(new SnapshotHandler(snapshot)))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    var handle = form.Handle;
                    Invoke(form, "DisplayJob", new JobViewDto { Id = id, State = "Cancelled" });
                    Invoke(form, "PollTimer_Tick", null, EventArgs.Empty);
                    PumpUntil(() => !Field<bool>(form, "_polling"));
                    Assert.AreEqual(1, Field<DataGridView>(form, "activityGrid").Rows.Count);
                }
            });
        }

        [TestMethod]
        public void DelayedCancellationSnapshot_CannotOverwriteNewerExecutionResults()
        {
            OnSta(() =>
            {
                var id = Guid.NewGuid();
                var handler = new DelayedCancelHandler(id);
                using (var http = new HttpClient(handler))
                using (var client = new AgentHostClient(http))
                using (var form = new MainForm(client))
                {
                    var handle = form.Handle;
                    Invoke(form, "DisplayJob", new JobViewDto { Id = id, State = "Executing" });
                    Invoke(form, "CancelButton_Click", null, EventArgs.Empty);
                    PumpUntil(() => handler.GetStarted);
                    Invoke(form, "DisplayJob", new JobViewDto { Id = id, State = "Cancelled",
                        Commands = Newtonsoft.Json.Linq.JArray.Parse("[{ 'sequenceNumber':1, 'revisionNumber':1, 'commandName':'Extrude', 'success':true }]") });
                    handler.CompleteGet();
                    PumpUntil(() => !Field<bool>(form, "_cancelling"));
                    Assert.AreEqual(1, Field<DataGridView>(form, "activityGrid").Rows.Count);
                }
            });
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && timer.Elapsed.TotalSeconds < 5) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.IsTrue(condition());
        }

        private sealed class SnapshotHandler : HttpMessageHandler
        {
            private readonly JobViewDto _snapshot;
            public SnapshotHandler(JobViewDto snapshot) { _snapshot = snapshot; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(_snapshot)) });
        }

        private sealed class DelayedCancelHandler : HttpMessageHandler
        {
            private readonly Guid _id;
            private readonly TaskCompletionSource<HttpResponseMessage> _get = new TaskCompletionSource<HttpResponseMessage>();
            public bool GetStarted { get; private set; }
            public DelayedCancelHandler(Guid id) { _id = id; }
            private HttpResponseMessage Response() => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(new JobViewDto { Id = _id, State = "Cancelled" })) };
            public void CompleteGet() => _get.SetResult(Response());
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(Response());
                GetStarted = true;
                return _get.Task;
            }
        }
        [TestMethod]
        public void PendingAction_DisablesApprovalAndRestoresOnlyEligibleActions()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new MainForm(client))
                {
                    Invoke(form, "DisplayJob", new JobViewDto { Id = Guid.NewGuid(), State = "AwaitingApproval",
                        PlanValidated = true, CurrentRevisionId = Guid.NewGuid() });
                    Assert.IsTrue(Field<Button>(form, "approveButton").Enabled);
                    Invoke(form, "SetBusy", true);
                    Assert.IsFalse(Field<Button>(form, "approveButton").Enabled,
                        "A second approval must be blocked while a request is pending.");
                    Invoke(form, "SetBusy", false);
                    Assert.IsTrue(Field<Button>(form, "approveButton").Enabled);
                }
            });
        }

        [TestMethod]
        public void AmbiguousPlan_CannotBeApprovedEvenIfValidationFlagIsSet()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new MainForm(client))
                {
                    Invoke(form, "DisplayJob", new JobViewDto { Id = Guid.NewGuid(), State = "AwaitingApproval",
                        PlanValidated = true, HasUnresolvedAmbiguity = true, CurrentRevisionId = Guid.NewGuid() });
                    Assert.IsFalse(Field<Button>(form, "approveButton").Enabled);
                }
            });
        }

        [DataTestMethod]
        [DataRow("Cancelled")]
        [DataRow("ReadyForReview")]
        [DataRow("Failed")]
        public void TerminalJob_CannotBeCancelled(string state)
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new MainForm(client))
                {
                    Invoke(form, "DisplayJob", new JobViewDto { Id = Guid.NewGuid(), State = state });
                    Assert.IsFalse(Field<Button>(form, "cancelButton").Enabled);
                }
            });
        }

        internal static T Field<T>(object instance, string name) =>
            (T)(instance is MainForm ? typeof(MainForm) : instance.GetType()).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);

        internal static void Invoke(object instance, string method, params object[] args) =>
            (instance is MainForm ? typeof(MainForm) : instance.GetType()).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args);

        internal static void OnSta(Action action)
        {
            Exception failure = null;
            using (var settled = new ManualResetEventSlim())
            {
                Host.Value.Control.BeginInvoke(new MethodInvoker(() =>
                {
                    try { action(); } catch (Exception ex) { failure = ex; } finally { settled.Set(); }
                }));
                Assert.IsTrue(settled.Wait(TimeSpan.FromSeconds(20)), "Desktop test did not finish.");
            }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class StaHost
        {
            public Control Control { get; private set; }
            public StaHost()
            {
                using (var ready = new ManualResetEventSlim())
                {
                    var thread = new Thread(() =>
                    {
                        Application.EnableVisualStyles();
                        Control = new Control();
                        var handle = Control.Handle;
                        ready.Set();
                        Application.Run();
                        Control.Dispose();
                    }) { IsBackground = true };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(10)));
                }
            }
        }

        [TestMethod]
        public void MinimumLayout_KeepsInterpretationAndComposerUsable()
        {
            OnSta(() =>
            {
                using (var client = new AgentHostClient(new HttpClient()))
                using (var form = new PreviewForm(client))
                {
                    form.Size = form.MinimumSize;
                    PrepareForRender(form);
                    form.PerformLayout();
                    Capture(form, "workspace-minimum.png");
                    Assert.IsTrue(Field<TextBox>(form, "briefTextBox").Height >= 60,
                        "The agent interpretation must remain readable at the minimum size. Actual: " + Field<TextBox>(form, "briefTextBox").Height + ", client: " + form.ClientSize);
                    Assert.IsTrue(Field<TextBox>(form, "promptTextBox").Height >= 60);
                    Assert.IsTrue(Field<Button>(form, "requestChangesButton").Right <=
                        Field<Button>(form, "requestChangesButton").Parent.ClientSize.Width,
                        "Revision action must not be clipped at minimum width.");
                    form.ClientSize = new Size(1440, 900);
                    form.PerformLayout();
                    Capture(form, "workspace-empty.png");
                }
            });
        }

        internal static void Capture(Form form, string name)
        {
            var directory = Environment.GetEnvironmentVariable("CAD_AGENT_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        internal static void PrepareForRender(Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show();
            Application.DoEvents();
        }

        internal sealed class PreviewForm : MainForm
        {
            public PreviewForm(AgentHostClient client) : base(client) { }
            protected override void OnShown(EventArgs e) { }
        }
    }
}
