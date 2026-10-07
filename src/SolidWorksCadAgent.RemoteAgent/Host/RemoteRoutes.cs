using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Host
{
    public sealed class RemoteRoutes
    {
        private readonly RemotePairingCoordinator pairing;
        private readonly RemoteSessionCoordinator sessions;
        private readonly IRemoteCapture capture;
        private readonly IRemoteClock clock;
        private readonly Func<string, string, string, RemoteResponse> agentProxy;

        public RemoteRoutes(RemotePairingCoordinator pairing, RemoteSessionCoordinator sessions, IRemoteCapture capture, IRemoteClock clock)
            : this(pairing, sessions, capture, clock, null)
        {
        }

        public RemoteRoutes(
            RemotePairingCoordinator pairing,
            RemoteSessionCoordinator sessions,
            IRemoteCapture capture,
            IRemoteClock clock,
            Func<string, string, string, RemoteResponse> agentProxy)
        {
            this.pairing = pairing;
            this.sessions = sessions;
            this.capture = capture;
            this.clock = clock;
            this.agentProxy = agentProxy;
        }

        private sealed class ActionBody
        {
            public string DeviceName { get; set; }
            public string DeviceId { get; set; }
            public string RequestId { get; set; }
            public long AuthorityEpoch { get; set; }
        }

        public RemoteResponse Handle(RemoteRequest request)
        {
            try
            {
                if (request == null || request.Path == null || !request.Path.StartsWith("/remote/v1/", StringComparison.Ordinal) || request.Path.Contains("?") || request.Path.Contains("#"))
                    throw Error(404, "route_unavailable", "Remote protocol version or route is unavailable.");

                var path = request.Path.Substring(11);
                Guid agentJobId;
                string agentJobAction;
                var isAgentJob = TryParseAgentJobPath(path, out agentJobId, out agentJobAction);
                var get = path == "display/frame" || path == "session/status" || path == "agent/status" || (isAgentJob && (agentJobAction == null || agentJobAction == "artifact"));
                if (request.Method != (get ? "GET" : "POST"))
                    throw Error(405, "method_unavailable", "This request method is unsupported.");
                if (!RemoteRequestPolicy.IsBodyBounded(request.Body ?? "", request.Path))
                    throw Error(413, "request_large", "The remote request is too large.");
                if (!get && !(request.ContentType ?? "").Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    throw Error(415, "content_type", "Use JSON for remote requests.");

                switch (path)
                {
                    case "pair/request": return Ok(pairing.RequestPairing(Token(request, "Pairing"), Read<ActionBody>(request).DeviceName));
                    case "pair/status": return Ok(pairing.Poll(Read<ActionBody>(request).RequestId, Token(request, "Receipt")));
                    case "session/create": return Ok(sessions.CreateSession(Read<ActionBody>(request).DeviceId, Token(request, "Device")));
                    case "session/renew": return Ok(sessions.RenewSession(Token(request, "Session"), Read<ActionBody>(request).DeviceId, request.DeviceCredential));
                    case "session/status": return Ok(sessions.Status(Token(request, "Session")));
                    case "session/heartbeat": return Ok(sessions.Heartbeat(Token(request, "Session")));
                    case "session/resume": return Ok(sessions.ResumeControl(Token(request, "Session")));
                    case "session/take-control": return Ok(sessions.TakeControl(Token(request, "Session")));
                    case "session/release":
                        sessions.ReleaseInput(Token(request, "Session"), Read<ActionBody>(request).AuthorityEpoch);
                        return Ok(new { success = true });
                    case "session/close":
                        sessions.Close(Token(request, "Session"));
                        return Ok(new { success = true });
                    case "input":
                        sessions.SubmitInput(Token(request, "Session"), Read<RemoteInputEvent>(request));
                        return Ok(new { success = true });
                    case "display/frame": return Frame(Token(request, "Session"));
                    case "agent/status": return ProxyAgent(Token(request, "Session"), "GET", "status", "");
                    case "agent/jobs":
                        return ProxyAgent(
                            Token(request, "Session"),
                            "POST",
                            "jobs",
                            ReadJobBody(request));
                    default:
                        if (isAgentJob)
                        {
                            var upstreamPath = "jobs/" + agentJobId.ToString("D") + (agentJobAction == null ? "" : "/" + agentJobAction);
                            var sessionToken = Token(request, "Session");
                            sessions.Status(sessionToken);
                            var body = LifecycleBody(request, agentJobAction);
                            return ProxyAgent(sessionToken, get ? "GET" : "POST", upstreamPath, body);
                        }
                        throw Error(404, "route_unavailable", "This remote route is unavailable.");
                }
            }
            catch (RemoteProtocolException ex)
            {
                return Response(ex.Status, new { code = ex.Code, message = ex.Message });
            }
            catch (JsonException)
            {
                return Response(400, new { code = "invalid_json", message = "The remote request is invalid." });
            }
            catch
            {
                return Response(503, new { code = "remote_unavailable", message = "The Windows remote agent is unavailable. Check its control window." });
            }
        }

        private RemoteResponse ProxyAgent(string sessionToken, string method, string path, string body)
        {
            sessions.Status(sessionToken);
            if (agentProxy == null)
                throw Error(503, "agent_host_unavailable", "The CAD Agent Host is unavailable on this workstation.");

            var response = agentProxy(method, path, body ?? "");
            if (response == null || response.Status < 100 || response.Status > 599 || response.Body == null ||
                Encoding.UTF8.GetByteCount(response.Body) > (path.EndsWith("/artifact", StringComparison.Ordinal) ? RemoteRequestPolicy.ArtifactResponseLimit : RemoteRequestPolicy.FrameResponseLimit))
                throw Error(503, "agent_host_invalid", "The CAD Agent Host returned an invalid response.");

            try { JToken.Parse(response.Body); }
            catch (JsonException) { throw Error(503, "agent_host_invalid", "The CAD Agent Host returned an invalid response."); }
            return response;
        }

        private static string ReadJobBody(RemoteRequest request)
        {
            var payload = JObject.Parse(request.Body ?? "{}");
            var prompt = payload["prompt"]?.Type == JTokenType.String ? (string)payload["prompt"] : null;
            prompt = prompt?.Trim();
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2000)
                throw Error(400, "prompt_invalid", "Enter a CAD instruction of 1â€“2000 characters.");
            if (payload["image"] == null) return JsonConvert.SerializeObject(new { prompt }, RemoteJson.Settings);
            var image = payload["image"] as JObject;
            var mediaType = image?["mediaType"]?.Type == JTokenType.String ? (string)image["mediaType"] : null;
            var encoded = image?["dataBase64"]?.Type == JTokenType.String ? (string)image["dataBase64"] : null;
            if ((mediaType != "image/jpeg" && mediaType != "image/png") || string.IsNullOrEmpty(encoded) || encoded.Length > 5592408)
                throw Error(400, "image_invalid", "Attach one JPEG or PNG image of at most 4 MiB.");
            return JsonConvert.SerializeObject(new { prompt, image = new { mediaType, dataBase64 = encoded } }, RemoteJson.Settings);
        }

        private static string LifecycleBody(RemoteRequest request, string action)
        {
            if (action == null || action == "artifact") return "";
            if (action == "cancel" || action == "complete") return "{}";
            var payload = JObject.Parse(request.Body ?? "{}");
            if (payload["revisionId"]?.Type != JTokenType.String || !Guid.TryParse((string)payload["revisionId"], out var revision))
                throw Error(400, "revision_required", "The current revisionId is required.");
            if (action == "approve") return JsonConvert.SerializeObject(new { revisionId = revision });
            var instructions = payload["instructions"]?.Type == JTokenType.String ? ((string)payload["instructions"]).Trim() : null;
            if (string.IsNullOrEmpty(instructions) || instructions.Length > 2000)
                throw Error(400, "instructions_invalid", "Enter change instructions of 1–2000 characters.");
            return JsonConvert.SerializeObject(new { revisionId = revision, instructions });
        }

        private static bool TryParseAgentJobPath(string path, out Guid id, out string action)
        {
            id = Guid.Empty;
            action = null;
            var parts = path.Split('/');
            if ((parts.Length != 3 && parts.Length != 4) || parts[0] != "agent" || parts[1] != "jobs" || !Guid.TryParse(parts[2], out id)) return false;
            if (parts.Length == 3) return true;
            action = parts[3];
            return action == "cancel" || action == "approve" || action == "request-changes" || action == "complete" || action == "artifact";
        }

        private RemoteResponse Frame(string token)
        {
            sessions.Status(token);
            var frame = capture.Capture();
            var now = clock.UtcNow;
            if (frame == null || frame.Width < 1 || frame.Height < 1 || frame.Width > 1600 || frame.Height > 1600 || frame.DisplayGeneration < 1 ||
                frame.JpegBytes == null || frame.JpegBytes.Length == 0 || frame.JpegBytes.Length > 2097152 ||
                !RemoteInputEvent.Coordinate(frame.CursorX) || !RemoteInputEvent.Coordinate(frame.CursorY) ||
                now - frame.CapturedAt > TimeSpan.FromSeconds(3) || frame.CapturedAt > now.AddSeconds(1))
                throw Error(503, "display_unavailable", "A current desktop image is unavailable. Check the Windows desktop.");
            frame.AgeAtResponseMs = Math.Max(0, (long)(now - frame.CapturedAt).TotalMilliseconds);
            sessions.UpdateDisplayGeneration(frame.DisplayGeneration);
            sessions.Status(token);
            return Ok(frame);
        }

        private static T Read<T>(RemoteRequest request) where T : class =>
            JsonConvert.DeserializeObject<T>(request.Body ?? "", RemoteJson.Settings) ?? throw Error(400, "invalid_json", "A JSON request is required.");

        private static string Token(RemoteRequest request, string scheme)
        {
            var header = request.Authorization;
            if (header == null || header.Length > 512 || !header.StartsWith(scheme + " ", StringComparison.Ordinal))
                throw Error(401, "authorization_required", "Remote authorization is required.");
            var token = header.Substring(scheme.Length + 1);
            if (token.Length < 1 || token.Length > 256)
                throw Error(401, "authorization_required", "Remote authorization is required.");
            return token;
        }

        private static RemoteResponse Ok(object value) => Response(200, value);

        private static RemoteResponse Response(int status, object value)
        {
            var body = JsonConvert.SerializeObject(value, RemoteJson.Settings);
            if (Encoding.UTF8.GetByteCount(body) > RemoteRequestPolicy.FrameResponseLimit)
                throw Error(503, "response_large", "The remote response is too large.");
            return new RemoteResponse { Status = status, Body = body };
        }

        private static RemoteProtocolException Error(int status, string code, string message) =>
            new RemoteProtocolException(status, code, message);
    }
}
