using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.Desktop
{
    public partial class MainForm : Form
    {
        private readonly AgentHostClient _client;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly List<JobViewDto> _history = new List<JobViewDto>();
        private JobViewDto _currentJob;

        public MainForm(AgentHostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InitializeComponent();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RefreshConnectionAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            base.OnFormClosed(e);
        }

        private async void AttachButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.AttachSolidWorksAsync(_lifetime.Token)));

        private async void LaunchButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.LaunchSolidWorksAsync(_lifetime.Token)));

        private async void SendButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(promptTextBox.Text))
            {
                SetStatus("Enter a CAD request first.", true);
                return;
            }

            await RunUiActionAsync(async () =>
            {
                DisplayJob(await _client.CreateJobAsync(promptTextBox.Text.Trim(), _lifetime.Token));
                promptTextBox.Clear();
            });
        }

        private async void ApproveButton_Click(object sender, EventArgs e)
        {
            if (_currentJob?.CurrentRevisionId == null) return;
            await RunUiActionAsync(async () => DisplayJob(await _client.ApproveJobAsync(
                _currentJob.Id,
                _currentJob.CurrentRevisionId.Value,
                _lifetime.Token)));
        }

        private async void CancelButton_Click(object sender, EventArgs e)
        {
            if (_currentJob == null) return;
            await RunUiActionAsync(async () => DisplayJob(await _client.CancelJobAsync(_currentJob.Id, _lifetime.Token)));
        }

        private void RequestChangesButton_Click(object sender, EventArgs e)
        {
            MessageBox.Show(
                this,
                "Clarification revisions are reserved in the V1 interface but are not enabled in this build. Cancel this job and submit a revised prompt.",
                "Request changes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            using (var form = new SettingsForm()) form.ShowDialog(this);
        }

        private async Task RefreshConnectionAsync()
        {
            await RunUiActionAsync(async () =>
            {
                var health = await _client.GetHealthAsync(_lifetime.Token);
                var solidWorks = await _client.GetSolidWorksStatusAsync(_lifetime.Token);
                hostStatusLabel.Text = "Agent Host: " + health.Status;
                DisplaySolidWorks(solidWorks);
            });
        }

        private async Task RunUiActionAsync(Func<Task> action)
        {
            SetBusy(true);
            try
            {
                await action();
                SetStatus("Ready", false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is AgentHostUnavailableException || ex is AgentHostApiException)
            {
                SetStatus(ex.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void DisplaySolidWorks(SolidWorksStatusDto status)
        {
            var version = status?.Runtime?.DisplayVersion;
            solidWorksStatusLabel.Text = status?.IsConnected == true
                ? "SOLIDWORKS: connected" + (string.IsNullOrWhiteSpace(version) ? string.Empty : " (" + version + ")")
                : "SOLIDWORKS: not connected";
        }

        private void DisplayJob(JobViewDto job)
        {
            _currentJob = job;
            if (job == null) return;
            currentJobLabel.Text = job.Id.ToString("D") + "  |  " + job.State;
            ambiguityTextBox.Text = job.AmbiguityMessage ?? "No unresolved ambiguities.";
            planTextBox.Text = job.Plan == null ? "No proposed plan." : job.Plan.ToString(Formatting.Indented);
            verificationTextBox.Text = job.Verifications == null
                ? "No verification results yet."
                : job.Verifications.ToString(Formatting.Indented);
            approveButton.Enabled = job.State == "AwaitingApproval" && job.PlanValidated && job.CurrentRevisionId.HasValue;
            cancelButton.Enabled = job.State != "Cancelled" && job.State != "ReadyForReview" && job.State != "Failed";
            requestChangesButton.Enabled = job.State == "AwaitingApproval" || job.State == "AwaitingClarification";

            _history.RemoveAll(item => item.Id == job.Id);
            _history.Insert(0, job);
            historyListBox.DataSource = null;
            historyListBox.DataSource = _history;
            historyListBox.DisplayMember = nameof(JobViewDto.Prompt);
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            sendButton.Enabled = !busy;
            attachButton.Enabled = !busy;
            launchButton.Enabled = !busy;
        }

        private void SetStatus(string message, bool error)
        {
            messageLabel.Text = message;
            messageLabel.ForeColor = error ? System.Drawing.Color.Firebrick : System.Drawing.Color.DarkGreen;
        }
    }
}
