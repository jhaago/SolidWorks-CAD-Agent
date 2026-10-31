using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class DesignIntakeClient : IDisposable
    {
        public const int MaximumReferenceBytes = 4 * 1024 * 1024;
        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        public DesignIntakeClient() : this(new HttpClient(), true) { }
        public DesignIntakeClient(HttpClient client) : this(client, false) { }
        private DesignIntakeClient(HttpClient client, bool ownsClient)
        {
            _http = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
            if (_http.BaseAddress == null) _http.BaseAddress = AgentHostClient.DefaultBaseAddress;
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }
        public async Task<JArray> ListAsync(CancellationToken token) => (JArray)await SendAsync(HttpMethod.Get, "designs", null, token, true).ConfigureAwait(false);
        public async Task<JObject> CreateAsync(string title, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Enter a design title.", nameof(title));
            return (JObject)await SendAsync(HttpMethod.Post, "designs", new JObject { ["title"] = title.Trim() }, token).ConfigureAwait(false);
        }
        public async Task<JObject> GetAsync(Guid id, CancellationToken token) => (JObject)await SendAsync(HttpMethod.Get, PathFor(id), null, token).ConfigureAwait(false);
        public async Task<JObject> AddReferenceAsync(Guid id, string fileName, byte[] bytes, string label, string viewType, CancellationToken token)
        {
            var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") throw new ArgumentException("Choose a PNG or JPEG image.");
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumReferenceBytes) throw new ArgumentException("Choose an image no larger than 4 MiB.");
            return (JObject)await SendAsync(HttpMethod.Post, PathFor(id) + "/references", new JObject
            {
                ["fileName"] = Path.GetFileName(fileName), ["base64"] = Convert.ToBase64String(bytes),
                ["label"] = label, ["viewType"] = viewType
            }, token).ConfigureAwait(false);
        }
        public async Task<JObject> SendMessageAsync(Guid id, string message, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter instructions or answers first.");
            return (JObject)await SendAsync(HttpMethod.Post, PathFor(id) + "/messages", new JObject { ["message"] = message.Trim() }, token).ConfigureAwait(false);
        }
        public async Task<JObject> ApproveAsync(Guid id, Guid revisionId, CancellationToken token) =>
            (JObject)await SendAsync(HttpMethod.Post, PathFor(id) + "/approve", Revision(revisionId), token).ConfigureAwait(false);
        public async Task<JObject> CreateCadPlanAsync(Guid id, Guid revisionId, CancellationToken token) =>
            (JObject)await SendAsync(HttpMethod.Post, PathFor(id) + "/plan-cad", Revision(revisionId), token).ConfigureAwait(false);
        public async Task<JObject> GetReferenceAsync(Guid id, Guid referenceId, CancellationToken token) =>
            (JObject)await SendAsync(HttpMethod.Get, PathFor(id) + "/references/" + referenceId.ToString("D"), null, token).ConfigureAwait(false);
        private static string PathFor(Guid id) => "designs/" + id.ToString("D");
        private static JObject Revision(Guid id) => new JObject { ["revisionId"] = id.ToString("D") };
        private async Task<JToken> SendAsync(HttpMethod method, string path, JObject body, CancellationToken token, bool array = false)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { throw new AgentHostUnavailableException(ex); }
                using (response)
                {
                    if (!response.IsSuccessStatusCode) throw new AgentHostApiException((int)response.StatusCode, "Design request could not be completed. Refresh the design and try again.");
                    if (response.Content.Headers.ContentLength > 6 * 1024 * 1024) throw new AgentHostApiException(200, "Design response was too large.");
                    string json;
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            if (output.Length + count > 6 * 1024 * 1024) throw new AgentHostApiException(200, "Design response was too large.");
                            output.Write(buffer, 0, count);
                        }
                        json = Encoding.UTF8.GetString(output.ToArray());
                    }
                    try
                    {
                        var value = JToken.Parse(json);
                        if (array ? value.Type != JTokenType.Array : value.Type != JTokenType.Object) throw new JsonException();
                        return value;
                    }
                    catch (JsonException) { throw new AgentHostApiException(200, "Agent Host returned an invalid design response."); }
                }
            }
        }
        public void Dispose() { if (_ownsClient) _http.Dispose(); }
    }
}
