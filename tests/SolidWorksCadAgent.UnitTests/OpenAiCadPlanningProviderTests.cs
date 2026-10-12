using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Security;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class OpenAiCadPlanningProviderTests
    {
        [TestMethod]
        public async Task PlanAsync_UsesStatelessStrictResponsesRequestAndParsesPlanUsage()
        {
            var handler = new RecordingHandler(PlanResponse(
                "Create a native plate.",
                new JArray(),
                new JArray(),
                new JArray(new JObject
                {
                    ["command"] = CadCommandNames.NewPart,
                    ["parameters_json"] = "{}"
                })));
            var provider = new OpenAiCadPlanningProvider(
                new HttpClient(handler),
                () => "test-secret-key",
                "gpt-5.6-sol");

            var result = await provider.PlanAsync(
                new CadPlanningRequest { Prompt = "Create a plate" },
                CancellationToken.None);

            Assert.AreEqual("openai", result.Provider);
            Assert.AreEqual("gpt-5.6-sol", result.Model);
            Assert.AreEqual("Create a native plate.", result.Summary);
            Assert.AreEqual(CadCommandNames.NewPart, result.ProposedCommands[0].Command);
            Assert.AreEqual(120, result.Usage.InputTokens);
            Assert.AreEqual(40, result.Usage.OutputTokens);

            Assert.AreEqual("https://api.openai.com/v1/responses", handler.RequestUri.AbsoluteUri);
            Assert.AreEqual("Bearer", handler.AuthorizationScheme);
            Assert.AreEqual("test-secret-key", handler.AuthorizationParameter);
            var request = JObject.Parse(handler.RequestBody);
            Assert.IsFalse((bool)request["store"]);
            Assert.IsFalse((bool)request["parallel_tool_calls"]);
            Assert.AreEqual("gpt-5.6-sol", (string)request["model"]);
            Assert.IsTrue((bool)request["tools"][0]["strict"]);
            Assert.IsFalse((bool)request["tools"][0]["parameters"]["additionalProperties"]);
            Assert.AreEqual("propose_cad_plan", (string)request["tool_choice"]["name"]);
            var commandSchema = request["tools"][0]["parameters"]["properties"]["commands"]["items"]["properties"]["command"];
            CollectionAssert.Contains(commandSchema["enum"].Values<string>().ToArray(), CadCommandNames.AddRectangle);
            StringAssert.Contains((string)request["instructions"], "widthMm");
            StringAssert.Contains((string)request["instructions"], "ThroughAll");
            StringAssert.Contains((string)request["instructions"], "normal to the sketch");
            StringAssert.Contains((string)request["instructions"], "create missing folders");
            CollectionAssert.Contains(commandSchema["enum"].Values<string>().ToArray(), "AddSlot");
            CollectionAssert.Contains(commandSchema["enum"].Values<string>().ToArray(), "AddRegularPolygon");
            StringAssert.Contains((string)request["instructions"], "Do not substitute an underside pocket for a requested top-face pocket");
        }

        [TestMethod]
        public async Task PlanAsync_AttachesImageToNormalCadPlanningRequest()
        {
            var handler = new RecordingHandler(PlanResponse("Needs a dimension", new JArray(), new JArray("What is the width?"), new JArray()));
            var provider = new OpenAiCadPlanningProvider(new HttpClient(handler), () => "test-secret-key", "gpt-5.6-sol");
            await provider.PlanAsync(new CadPlanningRequest
            {
                Prompt = "Model the bracket in this sketch",
                Clarifications = new[] { "Use a width of 40 mm" },
                Image = new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1, 2, 3 } }
            }, CancellationToken.None);
            var input = JObject.Parse(handler.RequestBody)["input"][0]["content"];
            StringAssert.Contains((string)input[0]["text"], "Use a width of 40 mm");
            Assert.AreEqual("input_image", (string)input[1]["type"]);
            Assert.AreEqual("data:image/jpeg;base64,AQID", (string)input[1]["image_url"]);
        }

        [TestMethod]
        public async Task PlanAsync_ParsesAmbiguityWithoutInventingCommands()
        {
            var handler = new RecordingHandler(PlanResponse(
                "Clarification required.",
                new JArray(),
                new JArray("Tapped or clearance hole is not specified."),
                new JArray()));
            var provider = new OpenAiCadPlanningProvider(new HttpClient(handler), () => "key", "gpt-5.6-sol");

            var result = await provider.PlanAsync(
                new CadPlanningRequest { Prompt = "Make a plate with an M8 hole" },
                CancellationToken.None);

            Assert.AreEqual(1, result.Ambiguities.Count);
            Assert.AreEqual(0, result.ProposedCommands.Count);
        }

        [TestMethod]
        public async Task PlanAsync_RejectsUnexpectedFunctionInsteadOfExecutingIt()
        {
            var response = new JObject
            {
                ["output"] = new JArray(new JObject
                {
                    ["type"] = "function_call",
                    ["name"] = "RunPowerShell",
                    ["call_id"] = "call_bad",
                    ["arguments"] = "{}"
                })
            }.ToString();
            var provider = new OpenAiCadPlanningProvider(
                new HttpClient(new RecordingHandler(response)),
                () => "key",
                "gpt-5.6-sol");

            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Do something" }, CancellationToken.None));

            Assert.AreEqual("UNEXPECTED_TOOL_CALL", error.Code);
        }

        [DataTestMethod]
        [DataRow("null")]
        [DataRow("42")]
        [DataRow("\"invalid-command\"")]
        [DataRow("[]")]
        public async Task PlanAsync_RejectsMalformedCommandEntryWithoutReturningPartialPlan(string invalidEntry)
        {
            var commands = new JArray(new JObject { ["command"] = CadCommandNames.NewPart, ["parameters_json"] = "{}" });
            commands.Add(JToken.Parse(invalidEntry));
            await AssertInvalidResponseAsync(PlanResponse("Create a plate", new JArray(), new JArray(), commands), "INVALID_PLAN_ARGUMENTS");
        }

        [DataTestMethod]
        [DataRow("summary", "42")]
        [DataRow("summary", "null")]
        [DataRow("summary", "{}")]
        [DataRow("assumptions", "[42]")]
        [DataRow("assumptions", "[false]")]
        [DataRow("assumptions", "[null]")]
        [DataRow("assumptions", "[{}]")]
        [DataRow("ambiguities", "[42]")]
        [DataRow("ambiguities", "[null]")]
        [DataRow("commands", "[{\"command\":42,\"parameters_json\":\"{}\"}]")]
        [DataRow("commands", "[{\"command\":{},\"parameters_json\":\"{}\"}]")]
        [DataRow("commands", "[{\"command\":\"NewPart\",\"parameters_json\":{}}]")]
        [DataRow("commands", "[{\"command\":\"NewPart\",\"parameters_json\":42}]")]
        public async Task PlanAsync_RejectsWrongPlanTokenTypes(string field, string invalidJson)
        {
            var response = JObject.Parse(PlanResponse("Create a plate", new JArray(), new JArray(), new JArray()));
            var arguments = JObject.Parse((string)response["output"][0]["arguments"]);
            arguments[field] = JToken.Parse(invalidJson);
            response["output"][0]["arguments"] = arguments.ToString();
            await AssertInvalidResponseAsync(response.ToString(), "INVALID_PLAN_ARGUMENTS");
        }

        [DataTestMethod]
        [DataRow("{\"summary\":\"First\",\"summary\":\"Second\",\"assumptions\":[],\"ambiguities\":[],\"commands\":[]}")]
        [DataRow("{\"summary\":\"Plate\",\"assumptions\":[],\"ambiguities\":[],\"commands\":[{\"command\":\"NewPart\",\"parameters_json\":\"{\\\"x\\\":1,\\\"x\\\":2}\"}]}")]
        [DataRow("{\"assumptions\":[],\"ambiguities\":[],\"commands\":[]}")]
        [DataRow("{\"summary\":\"Plate\",\"assumptions\":[],\"ambiguities\":[],\"commands\":[],\"extra\":true}")]
        [DataRow("{\"summary\":\"Plate\",\"assumptions\":[],\"ambiguities\":[],\"commands\":[{\"command\":\"NewPart\",\"parameters_json\":\"{}\",\"extra\":true}]}")]
        public async Task PlanAsync_RejectsPlanSchemaViolations(string arguments)
        {
            var response = JObject.Parse(PlanResponse("Plate", new JArray(), new JArray(), new JArray()));
            response["output"][0]["arguments"] = arguments;
            await AssertInvalidResponseAsync(response.ToString(), "INVALID_PLAN_ARGUMENTS");
        }

        [DataTestMethod]
        [DataRow("status", "\"incomplete\"")]
        [DataRow("status", "42")]
        [DataRow("call-status", "\"in_progress\"")]
        [DataRow("call-arguments", "42")]
        [DataRow("call-arguments", "{}")]
        [DataRow("output-entry", "null")]
        [DataRow("output-entry", "{\"type\":{}}")]
        [DataRow("model", "42")]
        [DataRow("model", "{}")]
        [DataRow("usage", "42")]
        [DataRow("usage", "{\"input_tokens\":\"120\",\"output_tokens\":40}")]
        [DataRow("usage", "{\"input_tokens\":1.5,\"output_tokens\":40}")]
        [DataRow("usage", "{\"input_tokens\":-1,\"output_tokens\":40}")]
        [DataRow("usage", "{\"input_tokens\":2147483648,\"output_tokens\":40}")]
        [DataRow("usage", "{\"input_tokens\":2147483647,\"output_tokens\":40}")]
        public async Task PlanAsync_RejectsMalformedResponseMetadataSafely(string field, string invalidJson)
        {
            var response = JObject.Parse(PlanResponse("Plate", new JArray(), new JArray(), new JArray()));
            var value = JToken.Parse(invalidJson);
            if (field == "call-status") response["output"][0]["status"] = value;
            else if (field == "call-arguments") response["output"][0]["arguments"] = value;
            else if (field == "output-entry") ((JArray)response["output"]).Add(value);
            else response[field] = value;
            await AssertInvalidResponseAsync(response.ToString(), "INVALID_OPENAI_RESPONSE");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PlanAsync_RejectsOversizedResponseBeforeReturningPlan(bool unknownLength)
        {
            var body = PlanResponse(new string('x', 256 * 1024), new JArray(), new JArray(), new JArray());
            using (var client = new HttpClient(new BodyHandler(body, unknownLength)))
            {
                var provider = new OpenAiCadPlanningProvider(client, () => "key", "test-model");
                var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                    provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
                Assert.AreEqual("INVALID_OPENAI_RESPONSE", error.Code);
            }
        }

        [TestMethod]
        [TimeoutAttribute(5000)]
        public async Task PlanAsync_StalledBodyAfterHeadersUsesClientTimeoutSafely()
        {
            using (var stream = new StalledBodyStream())
            using (var client = new HttpClient(new StalledBodyHandler(stream)) { Timeout = TimeSpan.FromMilliseconds(200) })
            {
                var provider = new OpenAiCadPlanningProvider(client, () => "private-key", "test-model");
                var pending = provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None);
                await stream.ReadStarted;
                var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() => pending);
                Assert.AreEqual("OPENAI_REQUEST_TIMEOUT", error.Code);
                Assert.IsFalse(error.ToString().Contains("private-key"));
            }
        }

        [TestMethod]
        [TimeoutAttribute(5000)]
        public async Task PlanAsync_StalledBodyAfterHeadersPreservesCallerCancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new StalledBodyStream())
            using (var client = new HttpClient(new StalledBodyHandler(stream)) { Timeout = Timeout.InfiniteTimeSpan })
            {
                var provider = new OpenAiCadPlanningProvider(client, () => "private-key", "test-model");
                var pending = provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, cancellation.Token);
                await stream.ReadStarted;
                cancellation.Cancel();
                try
                {
                    await pending;
                    Assert.Fail("Caller cancellation during the response body must propagate.");
                }
                catch (OperationCanceledException)
                {
                    Assert.IsTrue(cancellation.IsCancellationRequested);
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task HttpFailure_OversizedBodyKeepsSafeStatusDiagnostics(bool unknownLength)
        {
            var body = new JObject { ["error"] = new JObject
            {
                ["type"] = "rate_limit_error", ["code"] = "rate_limit_exceeded",
                ["message"] = "Authorization: Bearer private-key " + new string('x', 256 * 1024)
            } }.ToString();
            using (var client = new HttpClient(new BodyHandler(body, unknownLength, (HttpStatusCode)429)))
            {
                var provider = new OpenAiCadPlanningProvider(client, () => "private-key", "test-model");
                var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                    provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
                Assert.AreEqual("OPENAI_HTTP_ERROR", error.Code);
                Assert.AreEqual((int?)429, error.HttpStatusCode);
                StringAssert.Contains(error.Message, "HTTP 429");
                Assert.IsNull(error.OpenAiErrorCode);
                Assert.IsNull(error.OpenAiErrorType);
                Assert.IsFalse(error.ToString().Contains("private-key"));
                Assert.IsFalse(error.Message.Contains("Authorization"));
            }
        }

        private static async Task AssertInvalidResponseAsync(string body, string expectedCode)
        {
            using (var client = new HttpClient(new RecordingHandler(body)))
            {
                var provider = new OpenAiCadPlanningProvider(client, () => "private-key", "test-model");
                var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                    provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
                Assert.AreEqual(expectedCode, error.Code);
                Assert.IsFalse(error.ToString().Contains("private-key"));
            }
        }

        private sealed class BodyHandler : HttpMessageHandler
        {
            private readonly string _body;
            private readonly bool _unknownLength;
            private readonly HttpStatusCode _status;
            public BodyHandler(string body, bool unknownLength, HttpStatusCode status = HttpStatusCode.OK)
            {
                _body = body;
                _unknownLength = unknownLength;
                _status = status;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = _unknownLength
                        ? (HttpContent)new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(_body)))
                        : new StringContent(_body, Encoding.UTF8, "application/json")
                });
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] bytes) : base(bytes) { }
            public override bool CanSeek => false;
        }

        private sealed class StalledBodyHandler : HttpMessageHandler
        {
            private readonly Stream _stream;
            public StalledBodyHandler(Stream stream) { _stream = stream; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream) });
        }

        private sealed class StalledBodyStream : Stream
        {
            private readonly TaskCompletionSource<bool> _readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task ReadStarted => _readStarted.Task;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _readStarted.TrySetResult(true);
                // A missing body cancellation token fails promptly instead of hanging the test host.
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                throw new IOException("Authorization: Bearer private-key");
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [TestMethod]
        public async Task PlanAsync_RequiresCredentialBeforeSendingRequest()
        {
            var handler = new RecordingHandler("{}");
            var provider = new OpenAiCadPlanningProvider(new HttpClient(handler), () => null, "gpt-5.6-sol");

            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Create a plate" }, CancellationToken.None));

            Assert.AreEqual("OPENAI_CREDENTIAL_MISSING", error.Code);
            Assert.IsNull(handler.RequestUri);
        }

        [TestMethod]
        public async Task PlanAsync_ReadsOpenAiCredentialThroughSecretStoreBoundary()
        {
            var handler = new RecordingHandler(PlanResponse(
                "Create a native plate.", new JArray(), new JArray(), new JArray()));
            var secrets = new FakeSecretStore("stored-secret");
            var provider = new OpenAiCadPlanningProvider(
                new HttpClient(handler),
                secrets,
                "gpt-5.6-sol");

            await provider.PlanAsync(
                new CadPlanningRequest { Prompt = "Create a plate" },
                CancellationToken.None);

            Assert.AreEqual(OpenAiCadPlanningProvider.OpenAiCredentialTarget, secrets.LastGetTarget);
            Assert.AreEqual("stored-secret", handler.AuthorizationParameter);
        }

        [DataTestMethod]
        [DataRow(400, "invalid_request_error", "unsupported_parameter", "Unsupported parameter: temperature.")]
        [DataRow(401, "invalid_request_error", "invalid_api_key", "Incorrect API key provided: sk-project-secret-value")]
        [DataRow(429, "insufficient_quota", "insufficient_quota", "Check your billing and quota.")]
        [DataRow(500, "server_error", null, "Try again later.")]
        public async Task HttpFailure_PreservesSafeUsefulDiagnostics(int status, string type, string code, string message)
        {
            var body = new JObject { ["error"] = new JObject { ["type"] = type, ["code"] = code, ["message"] = message } }.ToString();
            var provider = new OpenAiCadPlanningProvider(new HttpClient(new ErrorHandler(status, body)),
                () => "sk-project-secret-value", "test-model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
            Assert.AreEqual("OPENAI_HTTP_ERROR", error.Code);
            StringAssert.Contains(error.Message, "HTTP " + status);
            StringAssert.Contains(error.Message, type);
            if (code != null) StringAssert.Contains(error.Message, code);
            Assert.IsFalse(error.ToString().Contains("sk-project-secret-value"));
            if (status != 401) StringAssert.Contains(error.Message, message);
        }

        [DataTestMethod]
        [DataRow("not JSON")]
        [DataRow("{\"error\":{\"message\":{\"unexpected\":\"shape\"},\"code\":[]}}")]
        [DataRow("{\"error\":null}")]
        public async Task HttpFailure_MalformedBodyKeepsStructuredFallback(string body)
        {
            var provider = new OpenAiCadPlanningProvider(new HttpClient(new ErrorHandler(502, body)), () => "key", "test-model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
            Assert.AreEqual("OPENAI_HTTP_ERROR", error.Code);
            StringAssert.Contains(error.Message, "HTTP 502");
            Assert.IsFalse(error.Message.Contains(body));
        }

        [TestMethod]
        public async Task HttpFailure_DoesNotEchoSecretsFromAnyErrorFieldOrHeaders()
        {
            const string secret = "private-configured-credential";
            var body = new JObject { ["error"] = new JObject
            {
                ["code"] = secret, ["type"] = "Bearer other-secret",
                ["message"] = "Authorization: Bearer other-secret; api_key=another-secret; token: private-token; " + secret + " sk-other-key",
                ["details"] = "password=secret-details"
            } }.ToString();
            var provider = new OpenAiCadPlanningProvider(new HttpClient(new ErrorHandler(401, body)), () => secret, "test-model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
            foreach (var value in new[] { secret, "other-secret", "another-secret", "private-token", "sk-other-key", "secret-details", "Authorization" })
                Assert.IsFalse(error.ToString().Contains(value), value);
        }

        [TestMethod]
        public async Task ProviderTimeout_IsStructuredWithoutLeakingTransportDetails()
        {
            var provider = new OpenAiCadPlanningProvider(new HttpClient(new TimeoutHandler()), () => "private-key", "test-model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() =>
                provider.PlanAsync(new CadPlanningRequest { Prompt = "Plate" }, CancellationToken.None));
            Assert.AreEqual("OPENAI_REQUEST_TIMEOUT", error.Code);
            Assert.IsFalse(error.ToString().Contains("private-key"));
        }

        private sealed class TimeoutHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new TaskCanceledException("Authorization: Bearer private-key");
        }

        private sealed class ErrorHandler : HttpMessageHandler
        {
            private readonly int _status;
            private readonly string _body;
            public ErrorHandler(int status, string body) { _status = status; _body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage((HttpStatusCode)_status) { Content = new StringContent(_body) });
        }

        private static string PlanResponse(string summary, JArray assumptions, JArray ambiguities, JArray commands)
        {
            var arguments = new JObject
            {
                ["summary"] = summary,
                ["assumptions"] = assumptions,
                ["ambiguities"] = ambiguities,
                ["commands"] = commands
            };
            return new JObject
            {
                ["output"] = new JArray(new JObject
                {
                    ["type"] = "function_call",
                    ["name"] = "propose_cad_plan",
                    ["call_id"] = "call_1",
                    ["arguments"] = arguments.ToString(Newtonsoft.Json.Formatting.None)
                }),
                ["usage"] = new JObject
                {
                    ["input_tokens"] = 120,
                    ["output_tokens"] = 40
                }
            }.ToString();
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly string _responseBody;

            public RecordingHandler(string responseBody)
            {
                _responseBody = responseBody;
            }

            public Uri RequestUri { get; private set; }
            public string AuthorizationScheme { get; private set; }
            public string AuthorizationParameter { get; private set; }
            public string RequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                AuthorizationScheme = request.Headers.Authorization?.Scheme;
                AuthorizationParameter = request.Headers.Authorization?.Parameter;
                RequestBody = await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class FakeSecretStore : ISecretStore
        {
            private readonly string _secret;

            public FakeSecretStore(string secret)
            {
                _secret = secret;
            }

            public string LastGetTarget { get; private set; }

            public void Set(string target, string secret) { throw new NotSupportedException(); }

            public string Get(string target)
            {
                LastGetTarget = target;
                return _secret;
            }

            public bool Exists(string target) { return Get(target) != null; }

            public void Delete(string target) { throw new NotSupportedException(); }
        }
    }
}
