using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.Contracts.Design;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class OpenAiImageDesignInterpreterTests
    {
        private static JObject Brief() => JObject.FromObject(new DesignInterpretation
        {
            Summary = "Bracket",
            ModellingStrategy = "Sketch and extrusion",
            Confidence = "low",
            Observations = {
 "Two visible holes" }
,
            Inferences = {
 "Likely mounting bracket" }
,
            Unknowns = {
 "Thickness" }
,
            Questions = {
 new DesignQuestion {
 Id = "thickness", Question = "What is the thickness?", Critical = true, Answer = null }
 }
        });
        private static string Response(JObject brief) => new JObject
        {
            ["status"] = "completed",
            ["output"] = new JArray(new JObject
            {
                ["type"] = "function_call",
                ["name"] = "interpret_design",
                ["arguments"] = brief.ToString(Formatting.None)
            }
)
        }
.ToString();
        private static ImageDesignRequest Request()
        {
            var result = new ImageDesignRequest
            {
                Design = new DesignSession
                {
                    Title = "Bracket"
                }
            };
            result.Design.Messages.Add(new DesignMessage
            {
                Role = "user",
                Text = "Thickness is 3 mm"
            });
            result.Design.Revisions.Add(new DesignBriefRevision
            {
                Number = 1,
                Brief = Brief().ToObject<DesignInterpretation>()
            });
            foreach (var type in new[] {
 "image/png", "image/jpeg" }
) result.Images.Add(new DesignReferenceContent
{
    Reference = new DesignReference
    {
        Id = Guid.NewGuid(),
        MediaType = type,
        Label = "Front",
        ViewType = "front",
        RelativePath = "private-path"
    }
,
    Bytes = new byte[] {
 1, 2, 3 }
});
            return result;
        }
        [TestMethod]
        public async Task InterpretAsync_SendsStatelessStrictImagesAndContext()
        {
            var handler = new Handler(Response(Brief()));
            var request = Request();
            var result = await new OpenAiImageDesignInterpreter(new HttpClient(handler), () => "secret", "configured-model").InterpretAsync(request, CancellationToken.None);
            Assert.AreEqual("Two visible holes", result.Observations[0]);
            Assert.AreEqual("Likely mounting bracket", result.Inferences[0]);
            Assert.AreEqual("Thickness", result.Unknowns[0]);
            Assert.IsTrue(result.Questions[0].Critical);
            Assert.IsNull(result.Questions[0].Answer);
            var payload = JObject.Parse(handler.Body);
            Assert.AreEqual("configured-model", (string)payload["model"]);
            Assert.IsFalse((bool)payload["store"]);
            Assert.IsFalse((bool)payload["parallel_tool_calls"]);
            Assert.AreEqual("interpret_design", (string)payload["tool_choice"]["name"]);
            Assert.IsTrue((bool)payload["tools"][0]["strict"]);
            var schema = payload["tools"][0]["parameters"];
            Assert.IsFalse((bool)schema["additionalProperties"]);
            Assert.AreEqual(((JObject)schema["properties"]).Count, ((JArray)schema["required"]).Count);
            Assert.IsFalse((bool)schema["properties"]["Questions"]["items"]["additionalProperties"]);
            var resolutionSchema = schema["properties"]["ResolvedUnknowns"]["items"];
            Assert.IsFalse((bool)resolutionSchema["additionalProperties"]);
            CollectionAssert.AreEqual(new[] { "Unknown", "Evidence" }, ((JArray)resolutionSchema["required"]).Values<string>().ToArray());
            StringAssert.Contains((string)payload["instructions"], "verbatim Evidence");
            var content = (JArray)payload["input"][0]["content"];
            Assert.AreEqual(2, content.Count(c => (string)c["type"] == "input_image"));
            StringAssert.Contains((string)content[2]["image_url"], "data:image/png;base64,AQID");
            StringAssert.Contains((string)content[4]["image_url"], "data:image/jpeg;base64,AQID");
            StringAssert.Contains((string)content[1]["text"], request.Images[0].Reference.Id.ToString());
            StringAssert.Contains((string)content[1]["text"], "Front");
            StringAssert.Contains((string)content[0]["text"], "PreviousRevision");
            StringAssert.Contains((string)content[0]["text"], "Thickness is 3 mm");
            Assert.IsFalse(handler.Body.Contains("private-path"));
            StringAssert.Contains((string)payload["instructions"], "widthMm");
        }
        [TestMethod]
        public void ParseInterpretation_BoundsResolutionEntries()
        {
            var brief = Brief();
            brief["ResolvedUnknowns"] = new JArray(new JObject { ["Unknown"] = "Thickness", ["Evidence"] = new string('x', 4001) });
            Assert.ThrowsException<OpenAiPlanningException>(() => OpenAiImageDesignInterpreter.ParseInterpretation(brief.ToString()));
            brief["ResolvedUnknowns"] = new JArray(Enumerable.Range(0, 101).Select(i => new JObject { ["Unknown"] = "Thickness", ["Evidence"] = "3 mm" }));
            Assert.ThrowsException<OpenAiPlanningException>(() => OpenAiImageDesignInterpreter.ParseInterpretation(brief.ToString()));
        }
        [TestMethod]
        public void ParseInterpretation_AcceptsEvidenceForSpecificUnknown()
        {
            var brief = Brief();
            brief["ResolvedUnknowns"] = new JArray(new JObject { ["Unknown"] = "Thickness", ["Evidence"] = "Thickness is 3 mm" });
            var result = OpenAiImageDesignInterpreter.ParseInterpretation(brief.ToString());
            Assert.AreEqual("Thickness", result.ResolvedUnknowns[0].Unknown);
            Assert.AreEqual("Thickness is 3 mm", result.ResolvedUnknowns[0].Evidence);
        }
        [DataTestMethod]
        [DataRow("[{\"Unknown\":42,\"Evidence\":\"3 mm\"}]")]
        [DataRow("[{\"Unknown\":\"Thickness\",\"Evidence\":false}]")]
        [DataRow("[{\"Unknown\":\"Thickness\"}]")]
        [DataRow("[{\"Unknown\":\"Thickness\",\"Evidence\":\"3 mm\",\"Extra\":true}]")]
        [DataRow("[null]")]
        [DataRow("null")]
        public void ParseInterpretation_RejectsInvalidResolutionTokens(string resolutions)
        {
            var brief = Brief(); brief["ResolvedUnknowns"] = JToken.Parse(resolutions);
            Assert.ThrowsException<OpenAiPlanningException>(() => OpenAiImageDesignInterpreter.ParseInterpretation(brief.ToString()));
        }
        [DataTestMethod]
        [DataRow("missing")]
        [DataRow("extra")]
        [DataRow("number")]
        [DataRow("null")]
        [DataRow("confidence")]
        [DataRow("questions")]
        [DataRow("critical")]
        [DataRow("answer")]
        [DataRow("long")]
        public void ParseInterpretation_RejectsSchemaViolations(string variant)
        {
            var brief = Brief();
            switch (variant)
            {
                case "missing":
                    brief.Remove("Unknowns");
                    break;
                case "extra":
                    brief["Commands"] = new JArray();
                    break;
                case "number":
                    brief["Observations"] = new JArray(42);
                    break;
                case "null":
                    brief["Summary"] = null;
                    break;
                case "confidence":
                    brief["Confidence"] = "certain";
                    break;
                case "questions":
                    brief["Questions"] = new JArray(Enumerable.Range(0, 4).Select(i => new JObject
                    {
                        ["Id"] = i.ToString(),
                        ["Question"] = "Q",
                        ["Critical"] = true,
                        ["Answer"] = null
                    }
));
                    break;
                case "critical":
                    brief["Questions"][0]["Critical"] = "true";
                    break;
                case "answer":
                    brief["Questions"][0]["Answer"] = 42;
                    break;
                case "long":
                    brief["Unknowns"] = new JArray(new string('x', 4001));
                    break;
            }
            Assert.ThrowsException<OpenAiPlanningException>(() => OpenAiImageDesignInterpreter.ParseInterpretation(brief.ToString()));
        }
        [DataTestMethod]
        [DataRow("not json")]
        [DataRow("{\"output\":[]}")]
        [DataRow("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"secret\"}]}]}")]
        [DataRow("{\"output\":[{\"type\":\"function_call\",\"name\":\"interpret_design\",\"arguments\":\"{}\"}]}")]
        public async Task InterpretAsync_RejectsMalformedOrRefusedOutputSafely(string body)
        {
            var provider = new OpenAiImageDesignInterpreter(new HttpClient(new Handler(body)), () => "secret", "model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() => provider.InterpretAsync(Request(), CancellationToken.None));
            Assert.IsFalse(error.ToString().Contains("secret"));
        }
        [TestMethod]
        public async Task InterpretAsync_BoundsResponseAndSuppressesHttpErrors()
        {
            foreach (var handler in new[] {
 new Handler(new string('x', 262145)), new Handler("Authorization: Bearer secret", HttpStatusCode.Unauthorized) }
)
            {
                var provider = new OpenAiImageDesignInterpreter(new HttpClient(handler), () => "secret", "model");
                var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() => provider.InterpretAsync(Request(), CancellationToken.None));
                Assert.IsFalse(error.ToString().Contains("secret"));
            }
        }
        [TestMethod]
        public async Task InterpretAsync_TimeoutIsSafe()
        {
            var provider = new OpenAiImageDesignInterpreter(new HttpClient(new Handler(null)), () => "secret", "model");
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() => provider.InterpretAsync(Request(), CancellationToken.None));
            Assert.AreEqual("OPENAI_REQUEST_TIMEOUT", error.Code);
            Assert.IsFalse(error.ToString().Contains("secret"));
        }
        [TestMethod]
        public async Task InterpretAsync_StalledBodyTimesOutSafely()
        {
            var provider = new OpenAiImageDesignInterpreter(new HttpClient(new StalledBodyHandler()), () => "secret", "model", TimeSpan.FromMilliseconds(100));
            var error = await Assert.ThrowsExceptionAsync<OpenAiPlanningException>(() => provider.InterpretAsync(Request(), CancellationToken.None));
            Assert.AreEqual("OPENAI_REQUEST_TIMEOUT", error.Code);
            Assert.IsFalse(error.ToString().Contains("secret"));
        }
        [TestMethod]
        public async Task InterpretAsync_StalledBodyPreservesCallerCancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(100);
                var provider = new OpenAiImageDesignInterpreter(new HttpClient(new StalledBodyHandler()), () => "secret", "model");
                try { await provider.InterpretAsync(Request(), cancellation.Token); Assert.Fail("Caller cancellation must propagate."); }
                catch (OperationCanceledException) { Assert.IsTrue(cancellation.IsCancellationRequested); }
            }
        }
        private sealed class StalledBodyHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        }
        private sealed class StalledStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly string _body;
            private readonly HttpStatusCode _status;
            public string Body
            {
                get;
                private set;
            }
            public Handler(string body, HttpStatusCode status = HttpStatusCode.OK)
            {
                _body = body;
                _status = status;
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Body = await request.Content.ReadAsStringAsync();
                if (_body == null) throw new TaskCanceledException("secret");
                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body)
                };
            }
        }
    }
}





