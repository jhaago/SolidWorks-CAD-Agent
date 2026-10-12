using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;
using SolidWorksCadAgent.Desktop.Ui;

namespace SolidWorksCadAgent.Desktop
{
    public partial class SettingsForm : Form
    {
        private readonly AgentHostClient _client;
        private readonly bool _ownsClient;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _closing, _saving;
        private AgentSettingsDto _loadedSettings;
        private JObject _settingsSnapshot;
        private bool _restartPending;

        public SettingsForm() : this(new AgentHostClient(), true) { }
        public SettingsForm(AgentHostClient client) : this(client, false) { }
        private SettingsForm(AgentHostClient client, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
            InitializeComponent();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await LoadSettingsAsync();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var result = await _client.GetSettingsAsync(timeout.Token);
                    var credential = await _client.GetCredentialStatusAsync(timeout.Token);
                    if (_closing) return;
                    if (result?.Settings == null) throw new InvalidOperationException("No settings snapshot returned.");
                    _settingsSnapshot = result.Settings;
                    _loadedSettings = result.Settings.ToObject<AgentSettingsDto>();
                    workspaceField.Text = _loadedSettings.WorkspaceRoot;
                    modelField.Text = _loadedSettings.OpenAiModel;
                    executableField.Text = _loadedSettings.SolidWorksExecutablePath;
                    loggingField.SelectedItem = loggingField.Items.Cast<string>().FirstOrDefault(value =>
                        string.Equals(value, _loadedSettings.LoggingLevel, StringComparison.OrdinalIgnoreCase));
                    executionField.SelectedItem = _loadedSettings.ExecutionMode;
                    autoModeField.Checked = _loadedSettings.AutoMode;
                    credentialLabel.Text = "OpenAI credential · " + (credential.Configured ? "Configured" : "Missing");
                    credentialLabel.ForeColor = credential.Configured ? Theme.Success : Theme.Warning;
                    fieldsPanel.Enabled = saveButton.Enabled = true;
                    SetMessage("Settings loaded. Review execution mode and approval policy before saving.", false);
                }
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) { DisplayError(ex); }
        }

        private async void SaveButton_Click(object sender, EventArgs e)
        {
            if (_saving || _loadedSettings == null) return;
            if (string.IsNullOrWhiteSpace(workspaceField.Text) || string.IsNullOrWhiteSpace(modelField.Text) ||
                loggingField.SelectedItem == null || executionField.SelectedItem == null)
            {
                SetMessage("Workspace, model, logging level and execution mode are required.", true);
                return;
            }
            _saving = true;
            fieldsPanel.Enabled = saveButton.Enabled = closeButton.Enabled = false;
            SetMessage("Saving settings to Agent Host…", false);
            try
            {
                var candidate = new AgentSettingsDto { WorkspaceRoot = workspaceField.Text.Trim(), OpenAiModel = modelField.Text.Trim(),
                    SolidWorksExecutablePath = string.IsNullOrWhiteSpace(executableField.Text) ? null : executableField.Text.Trim(),
                    LoggingLevel = loggingField.SelectedItem.ToString(), ExecutionMode = executionField.SelectedItem.ToString(),
                    AutoMode = autoModeField.Checked };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var settings = (JObject)_settingsSnapshot.DeepClone();
                    foreach (var property in JObject.FromObject(candidate).Properties())
                        settings[property.Name.Substring(0, 1).ToLowerInvariant() + property.Name.Substring(1)] = property.Value;
                    var result = await _client.UpdateSettingsAsync(settings, timeout.Token);
                    if (_closing) return;
                    if (result?.Settings == null) throw new InvalidOperationException("No saved settings snapshot returned.");
                    _settingsSnapshot = result.Settings;
                    _loadedSettings = result.Settings.ToObject<AgentSettingsDto>();
                    _restartPending |= result.RestartRequired;
                    SetMessage(_restartPending ? "Settings saved. Restart Agent Host to apply workspace, model, executable or execution mode changes." :
                        "Settings saved successfully.", false);
                    settingsMessageLabel.ForeColor = _restartPending ? Theme.Warning : Theme.Success;
                }
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) { DisplayError(ex); }
            finally
            {
                _saving = false;
                if (!_closing) fieldsPanel.Enabled = saveButton.Enabled = closeButton.Enabled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_saving) { e.Cancel = true; SetMessage("Wait for the settings save to finish before closing.", true); }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closing)
            {
                _closing = true;
                _lifetime.Cancel();
                _lifetime.Dispose();
                if (_ownsClient) _client.Dispose();
            }
            base.Dispose(disposing);
        }

        private void DisplayError(Exception ex) => SetMessage(ex is AgentHostApiException || ex is AgentHostUnavailableException ?
            JobPresentation.Redact(ex.Message) : "Settings could not be loaded or saved. Check Agent Host, then reopen Settings to retry.", true);

        private void SetMessage(string message, bool error)
        {
            if (_closing) return;
            settingsMessageLabel.Text = message;
            settingsMessageLabel.ForeColor = error ? Theme.Error : Theme.Muted;
        }
    }
}
