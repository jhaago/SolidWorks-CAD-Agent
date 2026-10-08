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
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
        }

        public RemoteResponse Forward(string method, string path, string body)
        {
            try
            {
                if (method == "GET" && path == "status") return BuildStatus();
                if (method == "POST" && path == "jobs") return Send("POST", "jobs/submit", body);

                if (TryJobPath(path, out var id, out var action))
                {
                    if (method == "GET" && (action == null || action == "artifact"))
                        return Send("GET", "jobs/" + id.ToString("D") + (action == null ? "" : "/artifact"), null);
                    if (method == "POST" && action != null && action != "artifact")
                        return Send("POST", "jobs/" + id.ToString("D") + "/" + action +
                            (action == "approve" || action == "request-changes" ? "-submit" : ""), body);
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
            var jobsResponse = jobsTask.GetAwaiter().GetResult();
            var jobs = JObject.Parse(jobsResponse.Body);
            var activeJob = (jobs["items"] as JArray)?.Children<JObject>().FirstOrDefault(item => !Terminal((string)item["state"]));
            // Fetch the current revision while the independent SOLIDWORKS status read is still running.
            // The list entry alone has no revision ID or clarification question, so it cannot drive
            // the phone's revision-bound response control.
            var activeJobTask = ReadActiveJobAsync(activeJob);
            Task.WhenAll(healthTask, settingsTask, solidWorksTask, activeJobTask).GetAwaiter().GetResult();
            var healthResponse = healthTask.Result;
            if (healthResponse.Status < 200 || healthResponse.Status >= 300) return healthResponse;
            var settingsResponse = settingsTask.Result;
            if (settingsResponse.Status < 200 || settingsResponse.Status >= 300) return settingsResponse;
            var solidWorksResponse = solidWorksTask.Result;
            if (jobsResponse.Status < 200 || jobsResponse.Status >= 300) return jobsResponse;

            var health = JObject.Parse(healthResponse.Body);
            var settingsRoot = JObject.Parse(settingsResponse.Body);
            var settings = settingsRoot["settings"] as JObject ?? new JObject();
            var solidWorks = JObject.Parse(solidWorksResponse.Body);

            return Json(200, new
            {
                agentHostAvailable = true,
                jobInputImages = (bool?)health["jobInputImages"] == true,
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
                activeJob = activeJob == null ? null : (object)(CompactActiveJob(activeJobTask.Result) ?? new JObject
                {
                    ["id"] = (string)activeJob["id"],
                    ["prompt"] = (string)activeJob["prompt"],
                    ["state"] = (string)activeJob["state"]
                })
            });
        }

        private static JObject CompactActiveJob(JObject detail)
        {
            if (detail == null) return null;
            var compact = new JObject();
            foreach (var name in new[] { "id", "prompt", "state", "currentRevisionId", "currentRevisionNumber",
                "planValidated", "hasUnresolvedAmbiguity", "ambiguityMessage", "plan", "verifications", "outputPath" })
            {
                if (detail[name] != null) compact[name] = detail[name].DeepClone();
            }
            return compact;
        }

        private async Task<JObject> ReadActiveJobAsync(JObject summary)
        {
            if (summary == null || !Guid.TryParse((string)summary["id"], out var id)) return null;
            try
            {
                var response = await SendAsync("GET", "jobs/" + id.ToString("D"), null).ConfigureAwait(false);
                if (response.Status < 200 || response.Status >= 300) return null;
                var detail = JObject.Parse(response.Body);
                return Guid.TryParse((string)detail["id"], out var returned) && returned == id ? detail : null;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                return null; // Keep workstation status available; the phone retries the full job read.
            }
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
            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(
                path.EndsWith("/artifact", StringComparison.Ordinal) || path == "jobs/submit" ? 15000 : 750)))
            using (var request = new HttpRequestMessage(method == "GET" ? HttpMethod.Get : HttpMethod.Post, path))
            {
                if (method == "POST") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(responseBody)) responseBody = "{}";
                    JToken.Parse(responseBody);
                    return new RemoteResponse { Status = (int)response.StatusCode, Body = responseBody };
                }
            }
        }

        private static bool TryJobPath(string path, out Guid id, out string action)
        {
            id = Guid.Empty;
            action = null;
            var parts = (path ?? "").Split('/');
            if ((parts.Length != 2 && parts.Length != 3) || parts[0] != "jobs" || !Guid.TryParse(parts[1], out id)) return false;
            if (parts.Length == 2) return true;
            action = parts[2];
            return action == "cancel" || action == "approve" || action == "request-changes" || action == "complete" || action == "artifact";
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
