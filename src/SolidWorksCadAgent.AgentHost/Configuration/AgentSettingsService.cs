using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Security;

namespace SolidWorksCadAgent.AgentHost.Configuration
{
    public sealed class AgentSettingsServiceException : InvalidOperationException
    {
        public AgentSettingsServiceException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }

    public sealed class AgentSettingsUpdateResult
    {
        public AgentSettings Settings { get; set; }
        public bool RestartRequired { get; set; }
    }

    public sealed class AgentSettingsService
    {
        private readonly AgentSettings _settings;
        private readonly JsonAgentSettingsStore _store;
        private readonly ISecretStore _secretStore;
        private readonly SqliteJobRepository _repository;
        private readonly SemaphoreSlim _updateGate = new SemaphoreSlim(1, 1);

        public AgentSettingsService(AgentSettings settings, JsonAgentSettingsStore store, ISecretStore secretStore, SqliteJobRepository repository)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public AgentSettings Current => _settings;

        public async Task<AgentSettingsUpdateResult> UpdateAsync(AgentSettings candidate, CancellationToken cancellationToken)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            candidate.HostPrefix = _settings.HostPrefix;
            candidate.Validate();

            await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (candidate.ExecutionMode != _settings.ExecutionMode &&
                    await _repository.HasNonTerminalJobsAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new AgentSettingsServiceException(
                        "ACTIVE_JOBS_BLOCK_MODE_CHANGE",
                        "Execution mode cannot change while a nonterminal CAD job exists.");
                }

                var restartRequired =
                    !string.Equals(candidate.WorkspaceRoot, _settings.WorkspaceRoot, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(candidate.OpenAiModel, _settings.OpenAiModel, StringComparison.Ordinal) ||
                    !string.Equals(candidate.SolidWorksExecutablePath, _settings.SolidWorksExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    candidate.ExecutionMode != _settings.ExecutionMode;

                _store.Save(candidate);
                Copy(candidate, _settings);
                return new AgentSettingsUpdateResult { Settings = _settings, RestartRequired = restartRequired };
            }
            finally
            {
                _updateGate.Release();
            }
        }

        public bool IsOpenAiCredentialConfigured() =>
            _secretStore.Exists(OpenAiCadPlanningProvider.OpenAiCredentialTarget);

        public void SetOpenAiCredential(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("A non-empty OpenAI API key is required.", nameof(apiKey));
            _secretStore.Set(OpenAiCadPlanningProvider.OpenAiCredentialTarget, apiKey.Trim());
        }

        public void DeleteOpenAiCredential() =>
            _secretStore.Delete(OpenAiCadPlanningProvider.OpenAiCredentialTarget);

        private static void Copy(AgentSettings source, AgentSettings destination)
        {
            destination.WorkspaceRoot = source.WorkspaceRoot;
            destination.AutoMode = source.AutoMode;
            destination.HostPrefix = source.HostPrefix;
            destination.OpenAiModel = source.OpenAiModel;
            destination.SolidWorksExecutablePath = source.SolidWorksExecutablePath;
            destination.LoggingLevel = source.LoggingLevel;
            destination.ExecutionMode = source.ExecutionMode;
        }
    }
}
