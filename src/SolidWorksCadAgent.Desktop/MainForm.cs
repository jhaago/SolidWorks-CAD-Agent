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
        private bool _busy;
        private bool _hostAvailable;
        private bool _showingConnectionError;

        public MainForm(AgentHostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InitializeComponent();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { await new AgentHostConnectionMonitor(_client).RunAsync(DisplayConnection, _lifetime.Token); }
            finally { _lifetime.Dispose(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lifetime.Cancel();
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

        private async void CompleteButton_Click(object sender, EventArgs e)
        {
            if (_currentJob?.State != "ReadyForReview") return;
            await RunUiActionAsync(async () => DisplayJob(await _client.CompleteJobAsync(_currentJob.Id, _lifetime.Token)));
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
            using (var form = new SettingsForm(_client)) form.ShowDialog(this);
        }

        private void DisplayConnection(HostConnectionSnapshot snapshot)
        {
            _hostAvailable = snapshot.IsAvailable;
            hostStatusLabel.Text = snapshot.IsAvailable ? "Agent Host: " + snapshot.Health.Status : "Agent Host: unavailable (retrying)";
            if (snapshot.IsAvailable && snapshot.SolidWorks != null) DisplaySolidWorks(snapshot.SolidWorks);
            else solidWorksStatusLabel.Text = "SOLIDWORKS: status unavailable";
            // Background connection checks must not overwrite a job/planner error or an active action.
            if (!_busy && !snapshot.IsAvailable)
            {
                SetStatus(snapshot.ErrorMessage, true);
                _showingConnectionError = true;
            }
            else if (!_busy && (_showingConnectionError || _currentJob == null)) SetStatus("Ready", false);
            UpdateActions();
        }

        private async Task RunUiActionAsync(Func<Task> action)
        {
            if (_busy || _lifetime.IsCancellationRequested) return;
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
                if (!_lifetime.IsCancellationRequested) SetBusy(false);
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
            UpdateActions();

            _history.RemoveAll(item => item.Id == job.Id);
            _history.Insert(0, job);
            historyListBox.DataSource = null;
            historyListBox.DataSource = _history;
            historyListBox.DisplayMember = nameof(JobViewDto.Prompt);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UseWaitCursor = busy;
            UpdateActions();
        }

        private void UpdateActions()
        {
            var enabled = !_busy && _hostAvailable;
            sendButton.Enabled = attachButton.Enabled = launchButton.Enabled = enabled;
            approveButton.Enabled = enabled && _currentJob?.State == "AwaitingApproval" &&
                _currentJob.PlanValidated && _currentJob.CurrentRevisionId.HasValue;
            completeButton.Enabled = enabled && _currentJob?.State == "ReadyForReview";
            cancelButton.Enabled = enabled && _currentJob != null && _currentJob.State != "Completed" &&
                _currentJob.State != "Cancelled" && _currentJob.State != "Failed";
            requestChangesButton.Enabled = enabled && (_currentJob?.State == "AwaitingApproval" || _currentJob?.State == "AwaitingClarification");
        }

        private void SetStatus(string message, bool error)
        {
            _showingConnectionError = false;
            messageLabel.Text = message;
            messageLabel.ForeColor = error ? System.Drawing.Color.Firebrick : System.Drawing.Color.DarkGreen;
        }
    }
}
