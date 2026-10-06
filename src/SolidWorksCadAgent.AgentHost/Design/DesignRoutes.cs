using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Host;

namespace SolidWorksCadAgent.AgentHost.Design
{
    public sealed class DesignRoutes
    {
        private readonly DesignIntakeService _service;
        private readonly ReferenceImageStore _images;
        public DesignRoutes(DesignIntakeService service, ReferenceImageStore images)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _images = images ?? throw new ArgumentNullException(nameof(images));
        }

        public async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken token)
        {
            var path = request.Path.Split('?')[0].TrimEnd('/');
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var method = request.Method.ToUpperInvariant();
            try
            {
                if (path == "/designs")
                {
                    if (method == "GET") return Json(200, await _service.ListAsync(token).ConfigureAwait(false));
                    if (method == "POST") return Json(201, await _service.CreateAsync(Text(Body(request), "title"), token).ConfigureAwait(false));
                }
                if (segments.Length < 2 || !Guid.TryParse(segments[1], out var id)) return Error(404, "not_found", "Design route was not found.");
                if (segments.Length == 2 && method == "GET") return Json(200, await _service.GetAsync(id, token).ConfigureAwait(false));
                if (segments.Length == 4 && segments[2] == "references" && method == "GET" && Guid.TryParse(segments[3], out var referenceId))
                {
                    var design = await _service.GetAsync(id, token).ConfigureAwait(false);
                    var reference = design.References.FirstOrDefault(r => r.Id == referenceId);
                    if (reference == null) return Error(404, "not_found", "Reference was not found in this design.");
                    return Json(200, new { fileName = reference.FileName, mediaType = reference.MediaType, base64 = Convert.ToBase64String(_images.Read(reference)) });
                }
                if (segments.Length != 3 || method != "POST") return Error(404, "not_found", "Design route was not found.");
                var body = Body(request);
                switch (segments[2])
                {
                    case "references":
                        var encoded = Text(body, "base64");
                        if (encoded.Length > ((ReferenceImageStore.MaxImageBytes + 2) / 3) * 4) return Error(413, "image_size", "Each image must contain at most 4 MiB.");
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(encoded); }
                        catch (FormatException) { return Error(400, "invalid_image", "The uploaded image encoding is invalid."); }
                        return Json(200, await _service.AddReferenceAsync(id, Text(body, "fileName"), bytes, OptionalText(body, "label"), OptionalText(body, "viewType"), token).ConfigureAwait(false));
                    case "messages":
                        return Json(200, await _service.DiscussAsync(id, Text(body, "message"), token).ConfigureAwait(false));
                    case "approve":
                        return Json(200, await _service.ApproveAsync(id, Revision(body), token).ConfigureAwait(false));
                    case "plan-cad":
                        return Json(200, await _service.PlanCadAsync(id, Revision(body), token).ConfigureAwait(false));
                    default: return Error(404, "not_found", "Design route was not found.");
                }
            }
            catch (DesignIntakeException ex)
            {
                var status = ex.Code == "not_found" ? 404 :
                    new[] { "approval_required", "stale_revision", "not_ready", "unsupported_features", "cad_already_planned", "revision_required" }.Contains(ex.Code) ? 409 : 400;
                return Error(status, ex.Code, ex.Message);
            }
            catch (JsonException) { return Error(400, "invalid_json", "A valid JSON object is required."); }
            catch (ArgumentException) { return Error(400, "invalid_request", "The design request contains invalid fields."); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return Error(503, "design_unavailable", "Design intake could not complete the request. Your saved discussion remains available."); }
        }
        private static JObject Body(AgentRequest request) => JObject.Parse(request.Body ?? "");
        private static string Text(JObject body, string name) => body[name]?.Type == JTokenType.String ? (string)body[name] : throw new ArgumentException("Invalid field");
        private static string OptionalText(JObject body, string name) => body[name] == null || body[name].Type == JTokenType.Null ? "" : Text(body, name);
        private static Guid Revision(JObject body) => Guid.TryParse(Text(body, "revisionId"), out var revision) ? revision : throw new ArgumentException("Invalid revision");
        private static AgentResponse Json(int status, object value) => new AgentResponse(status, JsonConvert.SerializeObject(value));
        private static AgentResponse Error(int status, string code, string message) => Json(status, new { error = new { code, message } });
    }
}
