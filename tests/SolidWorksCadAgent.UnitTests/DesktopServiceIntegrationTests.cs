using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Security;
using SolidWorksCadAgent.Desktop;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesktopServiceIntegrationTests
    {
        private const string Plate = "Create a 100 x 60 x 10 mm rectangular plate with one centred Ø20 through-hole.";

        [TestMethod]
        public async Task HttpClient_RoundTripsClarificationApprovalPersistedHistoryAndSettings()
        {
            using (var fixture = new SimulationFixture())
            {
                var client = fixture.Client;
                var job = await client.CreateJobAsync("Create a 100 x 60 x 10 mm plate with an M8 hole", CancellationToken.None);
                Assert.AreEqual("AwaitingClarification", job.State);
                var revision = job.CurrentRevisionId.Value;
                job = await client.RequestChangesAsync(job.Id, revision, "Use a 9 mm clearance through-hole", CancellationToken.None);
                Assert.AreEqual("AwaitingApproval", job.State);
                Assert.AreNotEqual(revision, job.CurrentRevisionId);
                Assert.AreEqual(2, job.CurrentRevisionNumber);
                Assert.IsTrue(job.IsSimulated);
                job = await client.ApproveJobAsync(job.Id, job.CurrentRevisionId.Value, CancellationToken.None);
                Assert.AreEqual("ReadyForReview", job.State);
                Assert.AreEqual(13, job.Commands.Count);
                Assert.AreEqual(3, job.Verifications.Count);
                var page = await client.ListJobsAsync(null, CancellationToken.None);
                Assert.AreEqual(job.Id, page.Items.Single().Id);
                Assert.IsTrue(page.Items.Single().IsSimulated);
                var snapshot = await client.GetJobAsync(job.Id, CancellationToken.None);
                Assert.AreEqual(2, snapshot.Revisions.Count);
                Assert.AreEqual(13, snapshot.Commands.Count);
                var settings = await client.GetSettingsAsync(CancellationToken.None);
                Assert.AreEqual("Simulation", (string)settings.Settings["executionMode"]);
                settings.Settings["openAiModel"] = "example-model-for-settings-test";
                var saved = await client.UpdateSettingsAsync(settings.Settings, CancellationToken.None);
                Assert.IsTrue(saved.RestartRequired);
                Assert.AreEqual("example-model-for-settings-test", (await client.GetSettingsAsync(CancellationToken.None)).Settings["openAiModel"]);
                Assert.IsFalse((await client.GetCredentialStatusAsync(CancellationToken.None)).Configured);
            }
        }

        [TestMethod]
        public void DesktopWorkflow_SubmitsApprovesAndReopensPersistedSimulationJob()
        {
            DesktopWorkflowTests.OnSta(() =>
            {
                using (var fixture = new SimulationFixture())
                using (var form = new DesktopWorkflowTests.PreviewForm(fixture.Client))
                {
                    DesktopWorkflowTests.PrepareForRender(form);
                    DesktopWorkflowTests.Invoke(form, "RefreshConnectionAsync");
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "sendButton").Enabled);
                    StringAssert.Contains(DesktopWorkflowTests.Field<Label>(form, "modeLabel").Text, "Simulation");
                    DesktopWorkflowTests.Field<TextBox>(form, "promptTextBox").Text = Plate;
                    DesktopWorkflowTests.Invoke(form, "SendButton_Click", null, EventArgs.Empty);
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "sendButton").Enabled);
                    Assert.IsTrue(DesktopWorkflowTests.Field<Button>(form, "approveButton").Enabled);
                    Assert.AreEqual(10, DesktopWorkflowTests.Field<DataGridView>(form, "planGrid").Rows.Count);
                    DesktopWorkflowTests.Capture(form, "workspace-plan.png");
                    DesktopWorkflowTests.Invoke(form, "ApproveButton_Click", null, EventArgs.Empty);
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "sendButton").Enabled);
                    StringAssert.Contains(DesktopWorkflowTests.Field<Label>(form, "currentJobLabel").Text, "Ready For Review");
                    Assert.IsFalse(DesktopWorkflowTests.Field<Button>(form, "approveButton").Enabled);
                    Assert.AreEqual(16, DesktopWorkflowTests.Field<DataGridView>(form, "activityGrid").Rows.Count);
                    DesktopWorkflowTests.Capture(form, "workspace-verified.png");
                    form.ClientSize = new System.Drawing.Size(1680, 1040);
                    DesktopWorkflowTests.Capture(form, "workspace-large.png");
                    DesktopWorkflowTests.Invoke(form, "NewJobButton_Click", null, EventArgs.Empty);
                    var history = DesktopWorkflowTests.Field<ListBox>(form, "historyListBox");
                    history.SelectedIndex = 0;
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "sendButton").Enabled);
                    Assert.AreEqual(16, DesktopWorkflowTests.Field<DataGridView>(form, "activityGrid").Rows.Count);
                    Assert.IsTrue(DesktopWorkflowTests.Field<TextBox>(form, "requestTextBox").Text.Contains("100"));
                }
            });
        }

        [TestMethod]
        public void Settings_LoadsHostValuesAndSavesOnlyOnExplicitAction()
        {
            DesktopWorkflowTests.OnSta(() =>
            {
                using (var fixture = new SimulationFixture())
                using (var form = new SettingsForm(fixture.Client))
                {
                    var existing = fixture.Client.GetSettingsAsync(CancellationToken.None).GetAwaiter().GetResult().Settings;
                    existing["loggingLevel"] = "information";
                    fixture.Client.UpdateSettingsAsync(existing, CancellationToken.None).GetAwaiter().GetResult();
                    DesktopWorkflowTests.PrepareForRender(form);
                    DesktopWorkflowTests.Invoke(form, "LoadSettingsAsync");
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "saveButton").Enabled);
                    Assert.AreEqual("Simulation", DesktopWorkflowTests.Field<ComboBox>(form, "executionField").SelectedItem);
                    Assert.AreEqual("Information", DesktopWorkflowTests.Field<ComboBox>(form, "loggingField").SelectedItem);
                    var model = DesktopWorkflowTests.Field<TextBox>(form, "modelField");
                    Assert.AreEqual("gpt-5.6-sol", model.Text);
                    DesktopWorkflowTests.Capture(form, "settings.png");
                    model.Text = "changed-model";
                    Assert.AreEqual("gpt-5.6-sol", fixture.Client.GetSettingsAsync(CancellationToken.None).GetAwaiter().GetResult().Settings["openAiModel"]);
                    DesktopWorkflowTests.Invoke(form, "SaveButton_Click", null, EventArgs.Empty);
                    PumpUntil(() => DesktopWorkflowTests.Field<Button>(form, "saveButton").Enabled);
                    Assert.AreEqual("changed-model", fixture.Client.GetSettingsAsync(CancellationToken.None).GetAwaiter().GetResult().Settings["openAiModel"]);
                    StringAssert.Contains(DesktopWorkflowTests.Field<Label>(form, "settingsMessageLabel").Text, "Restart Agent Host");
                }
            });
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.IsTrue(condition(), "Desktop action did not settle within the test deadline.");
            Application.DoEvents();
        }

        private sealed class SimulationFixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "CadAgentUi-" + Guid.NewGuid().ToString("N"));
            private readonly SqliteJobRepository _repository;
            private readonly HttpClient _http;
            public AgentHostClient Client { get; }
            public SimulationFixture()
            {
                Directory.CreateDirectory(_directory);
                _repository = new SqliteJobRepository(Path.Combine(_directory, "jobs.db"));
                _repository.InitializeAsync().GetAwaiter().GetResult();
                var routes = AgentHostComposition.CreateRoutesForMode(_repository,
                    new AgentSettings { ExecutionMode = ExecutionMode.Simulation },
                    new JsonAgentSettingsStore(Path.Combine(_directory, "settings.json")), new EmptySecrets(), null, null, null);
                _http = new HttpClient(new RoutesHandler(routes));
                Client = new AgentHostClient(_http);
            }
            public void Dispose() { Client.Dispose(); _http.Dispose(); _repository.Dispose(); Directory.Delete(_directory, true); }
        }

        private sealed class EmptySecrets : ISecretStore
        {
            public string Get(string target) => null;
            public bool Exists(string target) => false;
            public void Set(string target, string secret) => throw new AssertFailedException("UI must not provision credentials.");
            public void Delete(string target) => throw new AssertFailedException("UI must not delete credentials.");
        }

        private sealed class RoutesHandler : HttpMessageHandler
        {
            private readonly AgentRoutes _routes;
            public RoutesHandler(AgentRoutes routes) { _routes = routes; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                var response = await _routes.HandleAsync(new AgentRequest(request.Method.Method, request.RequestUri.PathAndQuery, body), cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage((HttpStatusCode)response.StatusCode) { Content = new StringContent(response.JsonBody, Encoding.UTF8, "application/json") };
            }
        }
    }
}
