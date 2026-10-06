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
        private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private readonly List<JobViewDto> _history = new List<JobViewDto>();
        private JobViewDto _currentJob;
        private bool _bindingHistory;
        private bool _historyLoaded;
        private string _nextHistoryCursor;
        private bool _busy;
        private bool _cancelling;
        private bool _hostAvailable;
        private bool _showingConnectionError;

        public MainForm(AgentHostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InitializeComponent();
            _refreshTimer.Tick += async (sender, args) => await RefreshDesktopStateAsync();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _refreshTimer.Start();
            try { await new AgentHostConnectionMonitor(_client).RunAsync(ConnectionChanged, _lifetime.Token); }
            finally { _lifetime.Dispose(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _lifetime.Cancel();
            base.OnFormClosed(e);
        }

        private async void AttachButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.AttachSolidWorksAsync(_lifetime.Token)));

        private async void LaunchButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.LaunchSolidWorksAsync(_lifetime.Token)));

        private async void SendButton_Click(object sender, EventArgs e)
        {
            if (_busy || JobRunning) return;
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
            if (_currentJob == null || _cancelling || _lifetime.IsCancellationRequested) return;
            var jobId = _currentJob.Id;
            _cancelling = true;
            UpdateActions();
            try
            {
                var result = await _client.CancelJobAsync(jobId, _lifetime.Token);
                if (_currentJob?.Id == jobId) DisplayJob(result);
                SetStatus("Cancellation saved. An in-progress CAD command may finish; subsequent commands will stop.", false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is AgentHostUnavailableException || ex is AgentHostApiException)
            {
                SetStatus(ex.Message, true);
            }
            finally
            {
                _cancelling = false;
                if (!_lifetime.IsCancellationRequested) UpdateActions();
            }
        }

        private async void CompleteButton_Click(object sender, EventArgs e)
        {
            if (_currentJob?.State != "ReadyForReview") return;
            await RunUiActionAsync(async () => DisplayJob(await _client.CompleteJobAsync(_currentJob.Id, _lifetime.Token)));
        }

        private async void RequestChangesButton_Click(object sender, EventArgs e)
        {
            if (_busy || _currentJob?.CurrentRevisionId == null) return;
            var jobId = _currentJob.Id;
            var revisionId = _currentJob.CurrentRevisionId.Value;
            using (var dialog = new RevisionInstructionsForm(_currentJob.AmbiguityMessage))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunUiActionAsync(async () => DisplayJob(await _client.RequestChangesAsync(
                    jobId, revisionId, dialog.Instructions, _lifetime.Token)));
            }
        }

        private async void ConnectionChanged(HostConnectionSnapshot snapshot)
        {
            DisplayConnection(snapshot);
            if (!snapshot.IsAvailable) _historyLoaded = false;
            if (snapshot.IsAvailable && !_historyLoaded && !_busy)
                await RunUiActionAsync(() => LoadHistoryAsync(false));
        }

        private async Task RefreshDesktopStateAsync()
        {
            if (_busy || !_hostAvailable || _lifetime.IsCancellationRequested) return;
            if (_historyLoaded && !JobRunning) return;
            await RunUiActionAsync(async () =>
            {
                if (JobRunning)
                {
                    var id = _currentJob.Id;
                    var snapshot = await _client.GetJobAsync(id, _lifetime.Token);
                    if (_currentJob?.Id == id) DisplayJob(snapshot);
                }
                if (!_historyLoaded) await LoadHistoryAsync(false);
            });
        }
        private bool JobRunning => _currentJob?.State == "Interpreting" ||
            _currentJob?.State == "Approved" || _currentJob?.State == "Executing" || _currentJob?.State == "Verifying";

        private async Task LoadHistoryAsync(bool append)
        {
            if (append && string.IsNullOrEmpty(_nextHistoryCursor)) return;
            var page = await _client.ListJobsAsync(25, append ? _nextHistoryCursor : null, _lifetime.Token);
            if (page?.Items == null) throw new AgentHostApiException(200, "Agent Host returned an invalid history page.");
            if (!append) _history.Clear();
            foreach (var item in page.Items)
                if (item != null && !_history.Exists(existing => existing.Id == item.Id)) _history.Add(item);
            _nextHistoryCursor = page.NextCursor;
            _historyLoaded = true;
            BindHistory();
            UpdateActions();
        }

        private async Task OpenHistoryJobAsync(Guid id)
        {
            if (_busy || !_hostAvailable || JobRunning) return;
            await RunUiActionAsync(async () => DisplayJob(await _client.GetJobAsync(id, _lifetime.Token)));
        }

        private async void HistoryListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_bindingHistory || !(historyListBox.SelectedItem is JobViewDto item)) return;
            await OpenHistoryJobAsync(item.Id);
        }

        private async void RefreshHistoryButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(() => LoadHistoryAsync(false));

        private async void OlderHistoryButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(() => LoadHistoryAsync(true));

        private void BindHistory()
        {
            _bindingHistory = true;
            try
            {
                historyListBox.DataSource = null;
                historyListBox.DisplayMember = nameof(JobViewDto.Prompt);
                historyListBox.DataSource = _history.ToArray();
                historyListBox.SelectedIndex = _currentJob == null ? -1 : _history.FindIndex(item => item.Id == _currentJob.Id);
            }
            finally { _bindingHistory = false; }
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
            // Approval and cancellation use independent requests. A late approval snapshot
            // must not replace a cancellation already acknowledged by the Host.
            if (_currentJob?.Id == job?.Id && _currentJob?.State == "Cancelled" && job?.State != "Cancelled") return;
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
            BindHistory();
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
            sendButton.Enabled = attachButton.Enabled = launchButton.Enabled = enabled && !JobRunning;
            approveButton.Enabled = enabled && _currentJob?.State == "AwaitingApproval" &&
                _currentJob.PlanValidated && _currentJob.CurrentRevisionId.HasValue;
            completeButton.Enabled = enabled && _currentJob?.State == "ReadyForReview";
            cancelButton.Enabled = _hostAvailable && !_cancelling && _currentJob != null && _currentJob.State != "Completed" &&
                _currentJob.State != "Cancelled" && _currentJob.State != "Failed";
            historyListBox.Enabled = enabled && !JobRunning;
            refreshHistoryButton.Enabled = enabled && !JobRunning;
            olderHistoryButton.Enabled = enabled && !JobRunning && !string.IsNullOrEmpty(_nextHistoryCursor);
            requestChangesButton.Enabled = enabled && _currentJob?.CurrentRevisionId != null && (_currentJob?.State == "AwaitingApproval" || _currentJob?.State == "AwaitingClarification");
        }

        private void SetStatus(string message, bool error)
        {
            _showingConnectionError = false;
            messageLabel.Text = message;
            messageLabel.ForeColor = error ? System.Drawing.Color.Firebrick : System.Drawing.Color.DarkGreen;
        }
    }
}
