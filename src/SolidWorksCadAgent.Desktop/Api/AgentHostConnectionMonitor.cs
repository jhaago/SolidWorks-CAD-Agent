using System;
using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class HostConnectionSnapshot
    {
        public bool IsAvailable { get; set; }
        public HostHealthDto Health { get; set; }
        public SolidWorksStatusDto SolidWorks { get; set; }
        public string ErrorMessage { get; set; }
    }

    // Retries only read-only probes. CAD writes are never replayed after a lost connection.
    public sealed class AgentHostConnectionMonitor
    {
        private readonly AgentHostClient _client;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public AgentHostConnectionMonitor(AgentHostClient client)
            : this(client, Task.Delay) { }

        public AgentHostConnectionMonitor(AgentHostClient client, Func<TimeSpan, CancellationToken, Task> delay)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        }

        public async Task RunAsync(Action<HostConnectionSnapshot> onChanged, CancellationToken cancellationToken)
        {
            if (onChanged == null) throw new ArgumentNullException(nameof(onChanged));
            var retrySeconds = 2;
            HostConnectionSnapshot previous = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var current = await ProbeAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (previous == null || previous.IsAvailable != current.IsAvailable ||
                        previous.ErrorMessage != current.ErrorMessage ||
                        previous.SolidWorks?.IsConnected != current.SolidWorks?.IsConnected ||
                        previous.SolidWorks?.Runtime?.DisplayVersion != current.SolidWorks?.Runtime?.DisplayVersion)
                        onChanged(current);
                    previous = current;
                    cancellationToken.ThrowIfCancellationRequested();
                    await _delay(TimeSpan.FromSeconds(current.IsAvailable ? 30 : retrySeconds), cancellationToken);
                    retrySeconds = current.IsAvailable ? 2 : Math.Min(30, retrySeconds * 2);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private static HostConnectionSnapshot UnavailableSnapshot(HostHealthDto health, string message) =>
            new HostConnectionSnapshot
            {
                IsAvailable = health?.Status == "ok",
                Health = health,
                ErrorMessage = health?.Status == "ok" ? "SOLIDWORKS status unavailable. Retrying automatically." : message
            };

        private async Task<HostConnectionSnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                HostHealthDto health = null;
                try
                {
                    health = await _client.GetHealthAsync(probe.Token);
                    if (health?.Status != "ok")
                        throw new AgentHostApiException(200, "Agent Host returned an invalid connection status.");
                    var solidWorks = await _client.GetSolidWorksStatusAsync(probe.Token);
                    if (solidWorks == null)
                        throw new AgentHostApiException(200, "Agent Host returned an invalid SOLIDWORKS status.");
                    return new HostConnectionSnapshot { IsAvailable = true, Health = health, SolidWorks = solidWorks };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return UnavailableSnapshot(health, "Agent Host did not respond. Retrying automatically.");
                }
                catch (Exception ex) when (ex is AgentHostUnavailableException || ex is AgentHostApiException)
                {
                    return UnavailableSnapshot(health, ex.Message);
                }
            }
        }
    }
}
