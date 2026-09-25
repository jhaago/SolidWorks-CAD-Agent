using System;
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
