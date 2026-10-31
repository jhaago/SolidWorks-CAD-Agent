using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;
using SolidWorksCadAgent.Desktop.Ui;

namespace SolidWorksCadAgent.Desktop
{
    public partial class MainForm : Form
    {
        private readonly AgentHostClient _client;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly List<JobViewDto> _history = new List<JobViewDto>();
        private readonly System.Windows.Forms.Timer _pollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        private JobViewDto _currentJob;
        private bool _busy, _closing, _bindingHistory, _polling, _executionPending, _cancelling;
        private string _nextCursor;
        private int _snapshotVersion;
        private bool _historyLoaded, _hostAvailable = true;

        public MainForm(AgentHostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InitializeComponent();
            _pollTimer.Tick += PollTimer_Tick;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _pollTimer.Start();
            await RefreshConnectionAsync();
            if (!_closing) await new AgentHostConnectionMonitor(_client).RunAsync(ConnectionChanged, _lifetime.Token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closing)
            {
                _closing = true;
                _pollTimer.Dispose();
                _lifetime.Cancel();
                _lifetime.Dispose();
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter))
            {
                if (!_busy) { if (JobPresentation.CanRevise(_currentJob)) requestChangesButton.PerformClick(); else sendButton.PerformClick(); }
                return true;
            }
            if (keyData == Keys.F5) { if (!_busy) refreshButton.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async void AttachButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.AttachSolidWorksAsync(_lifetime.Token)), "Attaching to SolidWorks…");

        private async void LaunchButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(async () => DisplaySolidWorks(await _client.LaunchSolidWorksAsync(_lifetime.Token)), "Launching SolidWorks…");

        private void DesignIntakeButton_Click(object sender, EventArgs e)
        {
            if (_busy || _cancelling) return;
            using (var form = new DesignIntakeForm(async jobId =>
            {
                if (!_closing) await RunUiActionAsync(async () =>
                    DisplayJob(await _client.GetJobAsync(jobId, _lifetime.Token)), "Loading the design plan…");
            })) form.ShowDialog(this);
        }

        private async void CompleteButton_Click(object sender, EventArgs e)
        {
            if (_busy || _currentJob?.State != "ReadyForReview") return;
            var id = _currentJob.Id;
            await RunUiActionAsync(async () => DisplayJob(await _client.CompleteJobAsync(id, _lifetime.Token)), "Completing the reviewed job…");
        }

        private async void SendButton_Click(object sender, EventArgs e)
        {
            if (_busy || JobPresentation.IsActive(_currentJob)) return;
            if (!HasInstructions()) return;
            var prompt = promptTextBox.Text.Trim();
            await RunUiActionAsync(async () =>
            {
                var job = await _client.CreateJobAsync(prompt, _lifetime.Token);
                if (_closing) return;
                DisplayJob(job);
                promptTextBox.Clear();
            }, "Planning the CAD request…");
        }

        private async void ApproveButton_Click(object sender, EventArgs e)
        {
            if (_busy || !JobPresentation.CanApprove(_currentJob)) return;
            var id = _currentJob.Id;
            var revision = _currentJob.CurrentRevisionId.Value;
            _executionPending = true;
            try
            {
                await RunUiActionAsync(async () =>
                {
                    await _client.ApproveJobAsync(id, revision, _lifetime.Token);
                    // Re-read after execution: concurrent cancellation can supersede the response.
                    await RefreshJobSnapshotAsync(id);
                    if (!_closing) reviewTabs.SelectedIndex = 1;
                }, "Executing the approved revision…");
            }
            finally { _executionPending = false; if (!_closing) UpdateActions(); }
        }

        private async void CancelButton_Click(object sender, EventArgs e)
        {
            if (_cancelling || !JobPresentation.CanCancel(_currentJob) || (_busy && !_executionPending)) return;
            var id = _currentJob.Id;
            _cancelling = true;
            _snapshotVersion++;
            UpdateActions();
            try
            {
                // Cancellation may run alongside approval. It does not abort an in-flight COM call.
                await _client.CancelJobAsync(id, _lifetime.Token);
                await RefreshJobSnapshotAsync(id);
                SetStatus("Cancellation recorded. Review activity for completed operations.", false);
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) { ShowError(ex); }
            finally { _cancelling = false; if (!_closing) UpdateActions(); }
        }

        private async void RequestChangesButton_Click(object sender, EventArgs e)
        {
            if (_busy || !JobPresentation.CanRevise(_currentJob) || !HasInstructions()) return;
            var id = _currentJob.Id;
            var revision = _currentJob.CurrentRevisionId.Value;
            var instructions = promptTextBox.Text.Trim();
            await RunUiActionAsync(async () =>
            {
                var job = await _client.RequestChangesAsync(id, revision, instructions, _lifetime.Token);
                if (_closing) return;
                DisplayJob(job);
                promptTextBox.Clear();
            }, "Planning the requested revision…");
        }

        private bool HasInstructions()
        {
            if (!string.IsNullOrWhiteSpace(promptTextBox.Text)) return true;
            SetStatus("Enter dimensions and feature details in the CAD instruction field first.", true);
            promptTextBox.Focus();
            return false;
        }

        private async void SettingsButton_Click(object sender, EventArgs e)
        {
            if (_busy || _cancelling) return;
            using (var form = new SettingsForm(_client)) form.ShowDialog(this);
            if (!_closing) await RefreshConnectionAsync();
        }

        private void NewJobButton_Click(object sender, EventArgs e)
        {
            if (_busy || _cancelling || JobPresentation.IsActive(_currentJob)) return;
            DisplayJob(null);
            _bindingHistory = true;
            historyListBox.ClearSelected();
            _bindingHistory = false;
            promptTextBox.Clear();
            promptTextBox.Focus();
        }

        private async void RefreshButton_Click(object sender, EventArgs e) => await RefreshConnectionAsync();
        private async void MoreHistoryButton_Click(object sender, EventArgs e) =>
            await RunUiActionAsync(() => LoadHistoryAsync(true), "Loading older jobs…");

        private async void HistoryListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_bindingHistory || _busy || _cancelling || !(historyListBox.SelectedItem is JobViewDto job)) return;
            if (_currentJob?.Id == job.Id) return;
            await OpenHistoryJobAsync(job.Id);
        }

        private async Task OpenHistoryJobAsync(Guid id)
        {
            if (_busy || _cancelling || !_hostAvailable || JobPresentation.IsActive(_currentJob)) return;
            await RunUiActionAsync(async () => DisplayJob(await _client.GetJobAsync(id, _lifetime.Token)), "Loading the selected job…");
        }

        private void DisplayConnection(HostConnectionSnapshot snapshot)
        {
            if (_closing) return;
            _hostAvailable = snapshot.IsAvailable;
            if (!_hostAvailable) _historyLoaded = false;
            hostStatusLabel.Text = snapshot.IsAvailable ? "Host · Online" : "Host · Offline (retrying)";
            hostStatusLabel.ForeColor = snapshot.IsAvailable ? Theme.Success : Theme.Error;
            if (snapshot.SolidWorks != null) DisplaySolidWorks(snapshot.SolidWorks);
            UpdateActions();
        }

        private async void ConnectionChanged(HostConnectionSnapshot snapshot)
        {
            DisplayConnection(snapshot);
            if (snapshot.IsAvailable && !_historyLoaded && !_busy) await RefreshDesktopStateAsync();
        }

        private async Task RefreshDesktopStateAsync()
        {
            if (_closing || _busy || _cancelling || !_hostAvailable) return;
            if (!_historyLoaded || JobPresentation.IsActive(_currentJob))
                await RunUiActionAsync(async () =>
                {
                    if (JobPresentation.IsActive(_currentJob)) await RefreshJobSnapshotAsync(_currentJob.Id);
                    if (!_historyLoaded) await LoadHistoryAsync(false);
                }, "Refreshing saved job state…");
        }

        private async Task RefreshConnectionAsync()
        {
            await RunUiActionAsync(async () =>
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var health = await _client.GetHealthAsync(timeout.Token);
                    var solidWorks = await _client.GetSolidWorksStatusAsync(timeout.Token);
                    var credential = await _client.GetCredentialStatusAsync(timeout.Token);
                    if (_closing) return;
                    DisplayConnection(new HostConnectionSnapshot { IsAvailable = true, Health = health, SolidWorks = solidWorks });
                    hostStatusLabel.Text = "Host · " + (health.Status == "ok" ? "Online" : JobPresentation.Redact(health.Status));
                    hostStatusLabel.ForeColor = health.Status == "ok" ? Theme.Success : Theme.Warning;
                    modeLabel.Text = "Active mode · " + (health.ExecutionMode ?? "Unknown") + "   /   AI credential · " +
                        (credential.Configured ? "Configured" : "Missing") + (health.ExecutionMode == "Simulation" ? " (local planner)" : string.Empty);
                    modeLabel.ForeColor = health.ExecutionMode == "Simulation" ? Theme.Warning : Theme.Muted;
                    DisplaySolidWorks(solidWorks);
                    await LoadHistoryPageAsync(false, timeout.Token);
                    if (_currentJob != null) DisplayJob(await _client.GetJobAsync(_currentJob.Id, timeout.Token));
                }
            }, "Refreshing connection and job history…");
        }

        private Task LoadHistoryAsync(bool append) => LoadHistoryPageAsync(append, _lifetime.Token);

        private async Task LoadHistoryPageAsync(bool append, CancellationToken cancellationToken)
        {
            var page = await _client.ListJobsAsync(append ? _nextCursor : null, cancellationToken);
            if (_closing) return;
            if (page?.Items == null) throw new AgentHostApiException(200, "Agent Host returned an invalid history page.");
            if (!append) _history.Clear();
            foreach (var item in page.Items ?? new JobViewDto[0])
                if (_history.All(existing => existing.Id != item.Id)) _history.Add(item);
            _nextCursor = page.NextCursor;
            _historyLoaded = true;
            BindHistory();
        }

        private void BindHistory()
        {
            _bindingHistory = true;
            historyListBox.BeginUpdate();
            try
            {
                historyListBox.Items.Clear();
                historyListBox.Items.AddRange(_history.Cast<object>().ToArray());
                historyListBox.DisplayMember = nameof(JobViewDto.Prompt);
                historyListBox.SelectedIndex = _currentJob == null ? -1 : _history.FindIndex(item => item.Id == _currentJob.Id);
            }
            finally { historyListBox.EndUpdate(); _bindingHistory = false; }
            UpdateActions();
        }

        private async Task RunUiActionAsync(Func<Task> action, string activity = "Working…")
        {
            if (_busy || _closing || _cancelling) return;
            _snapshotVersion++;
            SetBusy(true);
            SetStatus(activity, false);
            try
            {
                await action();
                SetStatus(_currentJob == null ? "Ready for a CAD request." : JobPresentation.StateTitle(_currentJob.State) + " · " +
                    (_currentJob.IsSimulated ? "Simulation" : "Real execution"), false);
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) { ShowError(ex); }
            finally { if (!_closing) SetBusy(false); }
        }

        private void ShowError(Exception ex)
        {
            if (_closing) return;
            if (ex is AgentHostUnavailableException)
            {
                _hostAvailable = false;
                _historyLoaded = false;
                hostStatusLabel.Text = "Host · Offline";
                hostStatusLabel.ForeColor = Theme.Error;
                solidWorksStatusLabel.Text = "SolidWorks · Status unknown";
                solidWorksStatusLabel.ForeColor = Theme.Muted;
                modeLabel.Text = "Connection unavailable · Start Agent Host, then Refresh";
            }
            SetStatus(ex is AgentHostApiException || ex is AgentHostUnavailableException ? JobPresentation.Redact(ex.Message) :
                ex is OperationCanceledException ? "Refresh timed out. Check Agent Host and retry." :
                "The request could not be completed. Check Agent Host and refresh before retrying.", true);
        }

        private async void PollTimer_Tick(object sender, EventArgs e)
        {
            if (!_closing && !_polling && !_historyLoaded && _hostAvailable && !_busy && !_cancelling && _currentJob?.State != "Cancelled")
                await RefreshDesktopStateAsync();
            // A cancelled job may still gain the result of its in-flight COM operation.
            // The host has no separate quiescence signal, so reconcile while it is displayed.
            if (_closing || _polling || _cancelling || _currentJob == null ||
                (!JobPresentation.IsActive(_currentJob) && _currentJob.State != "Cancelled" && !_executionPending)) return;
            var id = _currentJob.Id;
            var version = _snapshotVersion;
            _polling = true;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var job = await _client.GetJobAsync(id, timeout.Token);
                    if (!_closing && _currentJob?.Id == id && version == _snapshotVersion) DisplayJob(job);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closing && version == _snapshotVersion) ShowError(ex); }
            finally { _polling = false; }
        }

        private async Task RefreshJobSnapshotAsync(Guid id)
        {
            var version = _snapshotVersion;
            var snapshot = await _client.GetJobAsync(id, _lifetime.Token);
            if (!_closing && _currentJob?.Id == id && version == _snapshotVersion) DisplayJob(snapshot);
        }

        private void DisplaySolidWorks(SolidWorksStatusDto status)
        {
            if (_closing) return;
            var version = status?.Runtime?.DisplayVersion;
            solidWorksStatusLabel.Text = status?.IsConnected == true ? "SolidWorks · " + (version ?? "Connected") : "SolidWorks · Disconnected";
            solidWorksStatusLabel.ForeColor = status?.IsConnected == true ? Theme.Success : Theme.Muted;
            _tips.SetToolTip(solidWorksStatusLabel, JobPresentation.Redact(status?.ErrorMessage ?? version));
        }

        private void DisplayJob(JobViewDto job)
        {
            if (_closing) return;
            _snapshotVersion++;
            _currentJob = job;
            currentJobLabel.Text = job == null ? "Ready for a new design" : JobPresentation.StateTitle(job.State) +
                (job.IsSimulated ? " · Simulation" : string.Empty);
            currentJobLabel.ForeColor = job?.State == "Failed" ? Theme.Error : job?.State == "ReadyForReview" ? Theme.Success : Theme.Accent;
            _tips.SetToolTip(currentJobLabel, job?.Id.ToString("D") ?? "No job selected");
            requestTextBox.Text = JobPresentation.Redact(job?.Prompt ?? "No job selected. Start with a new CAD request below.");
            briefTextBox.Text = JobPresentation.Brief(job);
            workflowLabel.Text = JobPresentation.Guidance(job);
            ambiguityTextBox.Text = JobPresentation.Redact(job?.AmbiguityMessage ?? (job == null ?
                "Include dimensions, units, planes and feature intent." : "No unresolved design ambiguities reported."));
            var operations = JobPresentation.Operations(job).ToList();
            planSummaryLabel.Text = job == null ? "A validated plan is required before execution." :
                "Revision " + (job.CurrentRevisionNumber?.ToString() ?? "—") + "  /  " + operations.Count + " operations  /  " +
                (job.PlanValidated ? "Validated" : "Not validated");
            planGrid.DataSource = operations;
            activityGrid.DataSource = JobPresentation.Activity(job).ToList();
            activitySummaryLabel.Text = job == null ? "No execution records yet." : (job.Commands?.Count ?? 0) +
                " recorded operations · " + (job.Verifications?.Count ?? 0) + " verification checks";
            planTextBox.Text = job == null ? "No plan available." : JobPresentation.SafeJson(new JObject {
                ["jobId"] = job.Id, ["revisionId"] = job.CurrentRevisionId?.ToString(), ["plan"] = job.Plan?.DeepClone(),
                ["revisions"] = job.Revisions?.DeepClone() });
            verificationTextBox.Text = job == null ? "No execution records available." : JobPresentation.SafeJson(new JObject {
                ["state"] = job.State, ["isSimulated"] = job.IsSimulated, ["outputPath"] = job.OutputPath,
                ["commands"] = job.Commands?.DeepClone(), ["verifications"] = job.Verifications?.DeepClone() });
            composerLabel.Text = JobPresentation.CanRevise(job) ? "Clarify or revise this design" : "Describe a new CAD request";
            if (job != null)
            {
                var index = _history.FindIndex(item => item.Id == job.Id);
                if (index < 0) { _history.Insert(0, job); BindHistory(); }
                else
                {
                    _history[index] = job;
                    _bindingHistory = true;
                    historyListBox.Items[index] = job;
                    historyListBox.SelectedIndex = index;
                    _bindingHistory = false;
                    historyListBox.Invalidate();
                }
            }
            UpdateActions();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            busyProgress.Visible = busy;
            UpdateActions();
        }

        private void UpdateActions()
        {
            var idle = !_busy && !_cancelling;
            var connected = idle && _hostAvailable;
            refreshButton.Enabled = settingsButton.Enabled = idle;
            sendButton.Enabled = attachButton.Enabled = launchButton.Enabled = newJobButton.Enabled = connected && !JobPresentation.IsActive(_currentJob);
            historyListBox.Enabled = connected && !JobPresentation.IsActive(_currentJob);
            promptTextBox.Enabled = idle;
            moreHistoryButton.Enabled = connected && !JobPresentation.IsActive(_currentJob) && !string.IsNullOrEmpty(_nextCursor);
            attachmentButton.Enabled = idle;
            completeButton.Enabled = idle && _currentJob?.State == "ReadyForReview";
            approveButton.Enabled = idle && JobPresentation.CanApprove(_currentJob);
            requestChangesButton.Enabled = idle && JobPresentation.CanRevise(_currentJob);
            cancelButton.Enabled = !_cancelling && (!_busy || _executionPending) && JobPresentation.CanCancel(_currentJob);
        }

        private void SetStatus(string message, bool error)
        {
            if (_closing) return;
            messageLabel.Text = JobPresentation.Redact(message);
            messageLabel.ForeColor = error ? Theme.Error : Theme.Muted;
            _tips.SetToolTip(messageLabel, messageLabel.Text);
        }
    }
}
