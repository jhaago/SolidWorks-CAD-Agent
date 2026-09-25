using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Security;

namespace SolidWorksCadAgent.AgentHost.Ai
{
    public sealed class OpenAiCadPlanningProvider : ICadPlanningProvider
    {
        public const string OpenAiCredentialTarget = "SolidWorksCadAgent/OpenAI";
        private static readonly Uri ResponsesEndpoint = new Uri("https://api.openai.com/v1/responses");
        private readonly HttpClient _httpClient;
        private readonly Func<string> _getApiKey;
        private readonly string _model;

        public OpenAiCadPlanningProvider(HttpClient httpClient, Func<string> getApiKey, string model)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _getApiKey = getApiKey ?? throw new ArgumentNullException(nameof(getApiKey));
            _model = string.IsNullOrWhiteSpace(model)
                ? throw new ArgumentException("An OpenAI model is required.", nameof(model))
                : model;
        }

        public OpenAiCadPlanningProvider(HttpClient httpClient, ISecretStore secretStore, string model)
            : this(
                httpClient,
                () => (secretStore ?? throw new ArgumentNullException(nameof(secretStore))).Get(OpenAiCredentialTarget),
                model)
        {
        }

        public async Task<CadPlanningResult> PlanAsync(
            CadPlanningRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("A CAD prompt is required.", nameof(request));

            var apiKey = _getApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new OpenAiPlanningException("OPENAI_CREDENTIAL_MISSING", "No OpenAI API credential is configured.");

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                httpRequest.Content = new StringContent(
                    BuildRequest(request).ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    throw new OpenAiPlanningException("OPENAI_REQUEST_FAILED", "The OpenAI planning request could not be completed.", ex);
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new OpenAiPlanningException(
                            "OPENAI_HTTP_ERROR",
                            "OpenAI returned HTTP " + (int)response.StatusCode + " while planning the CAD job.");
                    }

                    return ParseResponse(body);
                }
            }
        }

        private JObject BuildRequest(CadPlanningRequest request)
        {
            var input = request.Prompt.Trim();
            if (request.Clarifications != null && request.Clarifications.Count > 0)
            {
                input += "\n\nUser clarifications:\n- " + string.Join("\n- ", request.Clarifications);
            }

            return new JObject
            {
                ["model"] = _model,
                ["store"] = false,
                ["parallel_tool_calls"] = false,
                ["instructions"] = "Interpret the engineering request into a safe proposed CAD plan. Do not claim the model has been built. List every unresolved ambiguity. Use millimetres. Return the plan only through propose_cad_plan.",
                ["input"] = input,
                ["tools"] = new JArray(BuildPlanTool()),
                ["tool_choice"] = new JObject
                {
                    ["type"] = "function",
                    ["name"] = "propose_cad_plan"
                }
            };
        }

        private static JObject BuildPlanTool()
        {
            return new JObject
            {
                ["type"] = "function",
                ["name"] = "propose_cad_plan",
                ["description"] = "Return a proposed native CAD plan for validation and human approval. This does not execute CAD commands.",
                ["strict"] = true,
                ["parameters"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["summary"] = new JObject { ["type"] = "string" },
                        ["assumptions"] = StringArraySchema(),
                        ["ambiguities"] = StringArraySchema(),
                        ["commands"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["command"] = new JObject { ["type"] = "string" },
                                    ["parameters_json"] = new JObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "A JSON object containing only the parameters for this CAD command."
                                    }
                                },
                                ["required"] = new JArray("command", "parameters_json"),
                                ["additionalProperties"] = false
                            }
                        }
                    },
                    ["required"] = new JArray("summary", "assumptions", "ambiguities", "commands"),
                    ["additionalProperties"] = false
                }
            };
        }

        private static JObject StringArraySchema()
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string" }
            };
        }

        private CadPlanningResult ParseResponse(string body)
        {
            JObject response;
            try
            {
                response = JObject.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new OpenAiPlanningException("INVALID_OPENAI_RESPONSE", "OpenAI returned invalid JSON.", ex);
            }

            var functionCalls = (response["output"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(item => (string)item["type"] == "function_call")
                .ToList();
            var unexpected = functionCalls.FirstOrDefault(item => (string)item["name"] != "propose_cad_plan");
            if (unexpected != null)
            {
                throw new OpenAiPlanningException(
                    "UNEXPECTED_TOOL_CALL",
                    "OpenAI requested an unsupported planning tool: " + (string)unexpected["name"]);
            }

            if (functionCalls.Count != 1)
                throw new OpenAiPlanningException("PLAN_TOOL_CALL_MISSING", "OpenAI did not return the required CAD planning tool call.");
            var call = functionCalls[0];

            JObject arguments;
            try
            {
                arguments = JObject.Parse((string)call["arguments"] ?? string.Empty);
            }
            catch (JsonException ex)
            {
                throw new OpenAiPlanningException("INVALID_PLAN_ARGUMENTS", "The CAD planning tool arguments were invalid JSON.", ex);
            }

            var result = new CadPlanningResult
            {
                Provider = "openai",
                Model = (string)response["model"] ?? _model,
                Summary = (string)arguments["summary"],
                Assumptions = ReadStrings(arguments["assumptions"]),
                Ambiguities = ReadStrings(arguments["ambiguities"]),
                ProposedCommands = ReadCommands(arguments["commands"]),
                Usage = new CadPlanningUsage
                {
                    InputTokens = (int?)response["usage"]?["input_tokens"] ?? 0,
                    OutputTokens = (int?)response["usage"]?["output_tokens"] ?? 0
                }
            };

            if (result.Ambiguities.Count > 0)
                result.ProposedCommands.Clear();
            return result;
        }

        private static List<string> ReadStrings(JToken token)
        {
            if (!(token is JArray array))
                throw new OpenAiPlanningException("INVALID_PLAN_ARGUMENTS", "The CAD plan contained an invalid string list.");
            return array.Values<string>().ToList();
        }

        private static List<CadCommandEnvelope> ReadCommands(JToken token)
        {
            if (!(token is JArray array))
                throw new OpenAiPlanningException("INVALID_PLAN_ARGUMENTS", "The CAD plan contained an invalid command list.");

            var commands = new List<CadCommandEnvelope>();
            foreach (var item in array.OfType<JObject>())
            {
                var name = (string)item["command"];
                if (string.IsNullOrWhiteSpace(name))
                    throw new OpenAiPlanningException("INVALID_PLAN_ARGUMENTS", "A proposed CAD command had no name.");

                JObject parameters;
                try
                {
                    parameters = JObject.Parse((string)item["parameters_json"] ?? string.Empty);
                }
                catch (JsonException ex)
                {
                    throw new OpenAiPlanningException("INVALID_PLAN_ARGUMENTS", "A proposed CAD command had invalid parameter JSON.", ex);
                }

                commands.Add(new CadCommandEnvelope { Command = name, Parameters = parameters });
            }
            return commands;
        }
    }
}
