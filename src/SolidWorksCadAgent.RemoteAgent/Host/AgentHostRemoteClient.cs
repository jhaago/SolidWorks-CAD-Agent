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

        public AgentHostRemoteClient()
        {
            client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = BaseAddress,
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        public RemoteResponse Forward(string method, string path, string body)
        {
            try
            {
                if (method == "GET" && path == "status") return BuildStatus();
                if (method == "POST" && path == "jobs") return Send("POST", "jobs", body);

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
            var healthResponse = Send("GET", "health", null);
            if (healthResponse.Status < 200 || healthResponse.Status >= 300) return healthResponse;
            var settingsResponse = Send("GET", "settings", null);
            if (settingsResponse.Status < 200 || settingsResponse.Status >= 300) return settingsResponse;
            var solidWorksResponse = Send("GET", "solidworks/status", null);
            if (solidWorksResponse.Status < 200 || solidWorksResponse.Status >= 300) return solidWorksResponse;
            var jobsResponse = Send("GET", "jobs?limit=25", null);
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
                    version = (string)solidWorks["runtime"]?["displayVersion"],
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

        private RemoteResponse Send(string method, string path, string body)
        {
            using (var request = new HttpRequestMessage(method == "GET" ? HttpMethod.Get : HttpMethod.Post, path))
            {
                if (method == "POST") request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
                using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                {
                    var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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
