using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadEngine.Adapter;

namespace SolidWorksCadAgent.Core.Backends
{
    /// <summary>
    /// Local prototype facade for a whole upstream job. Not registered in HTTP routes, UI, or JobCoordinator.
    /// Caller supplies an already approved plan and persists its identity before starting this opt-in backend.
    /// No legacy executor is accepted: backend selection is fixed for this facade's lifetime.
    /// </summary>
    public sealed class UpstreamBackendJob : IDisposable
    {
        private readonly McpBackendSession _session;
        private UpstreamBackendJob(McpBackendSession session) { _session = session; }
        public IReadOnlyDictionary<string, JObject> Tools => _session.Tools;
        public JObject ServerInfo => _session.ServerInfo;

        public static async Task<UpstreamBackendJob> StartAsync(EngineOptions options, Guid jobId,
            string journalPath, string leasePath = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (options == null || !options.Enabled) throw new InvalidOperationException("Upstream execution must be explicitly enabled.");
            if (jobId == Guid.Empty) throw new ArgumentException("A persisted job identity is required.", nameof(jobId));
            var boundary = new FileCadExecutionBoundary(leasePath ?? FileCadExecutionBoundary.DefaultLeasePath,
                jobId.ToString("D"), journalPath);
            return new UpstreamBackendJob(await McpBackendSession.StartAsync(options, boundary, cancellation).ConfigureAwait(false));
        }

        public Task<JObject> InvokeApprovedToolAsync(string name, JObject arguments,
            CancellationToken cancellation = default(CancellationToken)) => _session.CallToolAsync(name, arguments, cancellation);
        public void Dispose() { _session.Dispose(); }
    }
}
