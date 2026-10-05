using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.Desktop
{
    public partial class SettingsForm : Form
    {
        private readonly AgentHostClient _client;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private JObject _settings;
        private bool _busy;
        private bool _restartPending;

        public SettingsForm(AgentHostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InitializeComponent();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RunSettingsActionAsync(async token =>
            {
                var result = await _client.GetSettingsAsync(token);
                token.ThrowIfCancellationRequested();
                if (result?.Settings == null) throw new AgentHostApiException(200, "Agent Host returned no settings.");
                _settings = result.Settings;
                workspaceTextBox.Text = (string)_settings["workspaceRoot"];
                modelTextBox.Text = (string)_settings["openAiModel"];
                modeComboBox.SelectedItem = (string)_settings["executionMode"];
                executableTextBox.Text = (string)_settings["solidWorksExecutablePath"] ?? "Automatic detection";
                loggingTextBox.Text = (string)_settings["loggingLevel"];
                ShowStatus("Settings loaded from Agent Host.", false);
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lifetime.Cancel();
            base.OnFormClosed(e);
            if (!_busy) _lifetime.Dispose();
        }

        private async void SaveButton_Click(object sender, EventArgs e)
        {
            if (_settings == null || _busy) return;
            var model = modelTextBox.Text.Trim();
            if (model.Length == 0 || modeComboBox.SelectedItem == null)
            {
                ShowStatus("Enter an OpenAI model and select an execution mode.", true);
                return;
            }
            var candidate = (JObject)_settings.DeepClone();
            candidate["openAiModel"] = model;
            candidate["executionMode"] = (string)modeComboBox.SelectedItem;
            await RunSettingsActionAsync(async token =>
            {
                var result = await _client.UpdateSettingsAsync(candidate, token);
                token.ThrowIfCancellationRequested();
                if (result?.Settings == null) throw new AgentHostApiException(200, "Agent Host returned no saved settings.");
                _settings = result.Settings;
                _restartPending |= result.RestartRequired;
                ShowStatus(_restartPending
                    ? "Saved. Close and restart Agent Host to apply the changes."
                    : "Settings saved.", false);
            });
        }

        private async Task RunSettingsActionAsync(Func<CancellationToken, Task> action)
        {
            if (_busy || _lifetime.IsCancellationRequested) return;
            _busy = true;
            UpdateControls();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { await action(timeout.Token); }
                catch (OperationCanceledException)
                {
                    if (!_lifetime.IsCancellationRequested)
                        ShowStatus("Host request timed out. Reopen Settings to check saved values before retrying.", true);
                }
                catch (Exception ex) when (ex is AgentHostUnavailableException || ex is AgentHostApiException)
                {
                    if (!_lifetime.IsCancellationRequested) ShowStatus(ex.Message, true);
                }
                finally
                {
                    _busy = false;
                    if (_lifetime.IsCancellationRequested) _lifetime.Dispose();
                    else UpdateControls();
                }
            }
        }

        private void UpdateControls()
        {
            saveButton.Enabled = !_busy && _settings != null;
            modelTextBox.Enabled = modeComboBox.Enabled = saveButton.Enabled;
        }

        private void ShowStatus(string message, bool error)
        {
            statusLabel.Text = message;
            statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }
    }
}
