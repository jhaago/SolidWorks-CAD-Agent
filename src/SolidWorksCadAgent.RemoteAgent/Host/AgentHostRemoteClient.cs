using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.RemoteAgent.Host
{
    /// <summary>
    /// Narrow loopback-only bridge from the paired RemoteAgent to AgentHost.
    /// It deliberately exposes no arbitrary URL forwarding surface.
    /// </summary>
    public sealed class AgentHostRemoteClient : IDisposable
    {
        private static readonly Uri BaseAddress = new Uri("http://127.0.0.1:53741/");
        private readonly HttpClient client;
        private RemoteResponse lastSolidWorksStatus;

        public AgentHostRemoteClient() : this(new HttpClientHandler { UseProxy = false, MaxConnectionsPerServer = 8 }) { }

        public AgentHostRemoteClient(HttpMessageHandler handler)
        {
            client = new HttpClient(handler)
            {
                BaseAddress = BaseAddress,
                Timeout = TimeSpan.FromMilliseconds(750)
            };
        }

        public RemoteResponse Forward(string method, string path, string body)
        {
            try
            {
                if (method == "GET" && path == "status") return BuildStatus();
                if (method == "POST" && path == "jobs") return Send("POST", "jobs/submit", body);

                Guid id;
                if (TryJobPath(path, out id, out var cancel))
                {
                    if (!cancel && method == "GET") return Send("GET", "jobs/" + id.ToString("D"), null);
                    if (cancel && method == "POST") return Send("POST", "jobs/" + id.ToString("D") + "/cancel", "{}");
                }

                return Json(404, new { code = "route_unavailable", message = "This CAD Agent route is unavailable remotely." });
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException)
            {
                return Json(503, new { code = "agent_host_unavailable", message = "The CAD Agent Host is unavailable on this workstation." });
            }
            catch (JsonException)
            {
                return Json(503, new { code = "agent_host_invalid", message = "The CAD Agent Host returned an invalid response." });
            }
        }

        private RemoteResponse BuildStatus()
        {
            // Independent reads run together, bounded below the phone's two-second deadline.
            var healthTask = SendAsync("GET", "health", null);
            var settingsTask = SendAsync("GET", "settings", null);
            var solidWorksTask = ReadSolidWorksStatusAsync();
            var jobsTask = SendAsync("GET", "jobs?limit=25", null);
            Task.WhenAll(healthTask, settingsTask, solidWorksTask, jobsTask).GetAwaiter().GetResult();
            var healthResponse = healthTask.Result;
            if (healthResponse.Status < 200 || healthResponse.Status >= 300) return healthResponse;
            var settingsResponse = settingsTask.Result;
            if (settingsResponse.Status < 200 || settingsResponse.Status >= 300) return settingsResponse;
            var solidWorksResponse = solidWorksTask.Result;
            var jobsResponse = jobsTask.Result;
            if (jobsResponse.Status < 200 || jobsResponse.Status >= 300) return jobsResponse;

            var health = JObject.Parse(healthResponse.Body);
            var settingsRoot = JObject.Parse(settingsResponse.Body);
            var settings = settingsRoot["settings"] as JObject ?? new JObject();
            var solidWorks = JObject.Parse(solidWorksResponse.Body);
            var jobs = JObject.Parse(jobsResponse.Body);
            var activeJob = (jobs["items"] as JArray)?.Children<JObject>().FirstOrDefault(item => !Terminal((string)item["state"]));

            return Json(200, new
            {
                agentHostAvailable = true,
                executionMode = (string)settings["executionMode"] ?? (string)health["executionMode"],
                model = (string)settings["openAiModel"],
                solidWorks = new
                {
                    running = (bool?)solidWorks["isRunning"] ?? false,
                    attached = (bool?)solidWorks["isConnected"] ?? false,
                    visible = (bool?)solidWorks["isVisible"] ?? false,
                    version = (string)(solidWorks["runtime"] as JObject)?["displayVersion"],
                    activeDocument = (string)solidWorks["activeDocument"]
                },
                activeJob = activeJob == null ? null : new
                {
                    id = (string)activeJob["id"],
                    prompt = (string)activeJob["prompt"],
                    state = (string)activeJob["state"]
                }
            });
        }

        private async Task<RemoteResponse> ReadSolidWorksStatusAsync()
        {
            try
            {
                var response = await SendAsync("GET", "solidworks/status", null).ConfigureAwait(false);
                if (response.Status >= 200 && response.Status < 300)
                {
                    System.Threading.Interlocked.Exchange(ref lastSolidWorksStatus, response);
                    return response;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { }
            // CAD status shares the native dispatcher. Keep other job and heartbeat routes responsive.
            return System.Threading.Volatile.Read(ref lastSolidWorksStatus) ?? Json(200, new { });
        }

        private RemoteResponse Send(string method, string path, string body) =>
            SendAsync(method, path, body).GetAwaiter().GetResult();

        private async Task<RemoteResponse> SendAsync(string method, string path, string body)
        {
            using (var request = new HttpRequestMessage(method == "GET" ? HttpMethod.Get : HttpMethod.Post, path))
            {
                if (method == "POST") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(responseBody)) responseBody = "{}";
                    JToken.Parse(responseBody);
                    return new RemoteResponse { Status = (int)response.StatusCode, Body = responseBody };
                }
            }
        }

        private static bool TryJobPath(string path, out Guid id, out bool cancel)
        {
            id = Guid.Empty;
            cancel = false;
            const string prefix = "jobs/";
            if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var tail = path.Substring(prefix.Length);
            if (tail.EndsWith("/cancel", StringComparison.Ordinal))
            {
                cancel = true;
                tail = tail.Substring(0, tail.Length - 7);
            }
            return Guid.TryParse(tail, out id);
        }

        private static bool Terminal(string state) =>
            string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Cancelled", StringComparison.OrdinalIgnoreCase);

        private static RemoteResponse Json(int status, object value) => new RemoteResponse
        {
            Status = status,
            Body = JsonConvert.SerializeObject(value)
        };

        public void Dispose() => client.Dispose();
    }
}
