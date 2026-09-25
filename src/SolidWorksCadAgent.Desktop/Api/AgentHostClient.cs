using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class AgentHostClient : IDisposable
    {
        public static readonly Uri DefaultBaseAddress = new Uri("http://127.0.0.1:53741/");
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;

        public AgentHostClient()
            : this(new HttpClient(), true)
        {
        }

        public AgentHostClient(HttpClient httpClient)
            : this(httpClient, false)
        {
        }

        private AgentHostClient(HttpClient httpClient, bool ownsClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsClient = ownsClient;
            if (_httpClient.BaseAddress == null) _httpClient.BaseAddress = DefaultBaseAddress;
        }

        public Task<HostHealthDto> GetHealthAsync(CancellationToken cancellationToken) =>
            SendAsync<HostHealthDto>(HttpMethod.Get, "health", null, cancellationToken);

        public Task<SolidWorksStatusDto> GetSolidWorksStatusAsync(CancellationToken cancellationToken) =>
            SendAsync<SolidWorksStatusDto>(HttpMethod.Get, "solidworks/status", null, cancellationToken);

        public Task<SolidWorksStatusDto> AttachSolidWorksAsync(CancellationToken cancellationToken) =>
            SendAsync<SolidWorksStatusDto>(HttpMethod.Post, "solidworks/attach", new JObject(), cancellationToken);

        public Task<SolidWorksStatusDto> LaunchSolidWorksAsync(CancellationToken cancellationToken) =>
            SendAsync<SolidWorksStatusDto>(HttpMethod.Post, "solidworks/launch", new JObject(), cancellationToken);

        public Task<JobViewDto> CreateJobAsync(string prompt, CancellationToken cancellationToken) =>
            SendAsync<JobViewDto>(HttpMethod.Post, "jobs", new JObject { ["prompt"] = prompt }, cancellationToken);

        public Task<JobViewDto> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
            SendAsync<JobViewDto>(HttpMethod.Get, "jobs/" + jobId.ToString("D"), null, cancellationToken);

        public Task<JobViewDto> ApproveJobAsync(Guid jobId, Guid revisionId, CancellationToken cancellationToken) =>
            SendAsync<JobViewDto>(
                HttpMethod.Post,
                "jobs/" + jobId.ToString("D") + "/approve",
                new JObject { ["revisionId"] = revisionId.ToString("D") },
                cancellationToken);

        public Task<JobViewDto> CancelJobAsync(Guid jobId, CancellationToken cancellationToken) =>
            SendAsync<JobViewDto>(HttpMethod.Post, "jobs/" + jobId.ToString("D") + "/cancel", new JObject(), cancellationToken);

        private async Task<T> SendAsync<T>(HttpMethod method, string path, JObject body, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null)
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    throw new AgentHostUnavailableException(ex);
                }

                using (response)
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var message = "Agent Host returned HTTP " + (int)response.StatusCode + ".";
                        try
                        {
                            message = (string)JObject.Parse(json)["error"]?["message"] ?? message;
                        }
                        catch (JsonException) { }
                        throw new AgentHostApiException((int)response.StatusCode, message);
                    }

                    return JsonConvert.DeserializeObject<T>(json);
                }
            }
        }

        public void Dispose()
        {
            if (_ownsClient) _httpClient.Dispose();
        }
    }
}
