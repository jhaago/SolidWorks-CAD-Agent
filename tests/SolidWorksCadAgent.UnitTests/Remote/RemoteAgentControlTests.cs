using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.RemoteAgent.Host;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    [TestClass]
    public class RemoteAgentControlTests
    {
        private static RemoteRequest Request(string path, string token = null, string method = "POST", string body = "{}") => new RemoteRequest
        {
            Method = method,
            Path = "/remote/v1/" + path,
            Authorization = token == null ? null : "Session " + token,
            ContentType = method == "GET" ? null : "application/json",
            Body = body
        };

        private static RemoteRoutes CreateRoutes(SessionFixture fixture, Func<string, string, string, RemoteResponse> proxy)
        {
            var constructor = typeof(RemoteRoutes).GetConstructors().SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 5 && parameters[4].ParameterType == typeof(Func<string, string, string, RemoteResponse>);
            });
            Assert.IsNotNull(constructor, "RemoteRoutes must accept the bounded Agent Host proxy used by paired Android sessions.");
            return (RemoteRoutes)constructor.Invoke(new object[] { fixture.Pairing, fixture.Sessions, new TestCapture(), fixture.Clock, proxy });
        }

        [TestMethod]
        public void LifecycleRequiresSessionAndForwardsOnlyValidatedCurrentRevisionPayload()
        {
            var fixture = new SessionFixture();
            string forwardedPath = null, forwardedBody = null;
            var routes = CreateRoutes(fixture, (method, path, body) =>
            { forwardedPath = path; forwardedBody = body; return new RemoteResponse { Status = 202, Body = "{}" }; });
            var id = Guid.NewGuid();
            var revision = Guid.NewGuid();
            var body = new JObject { ["revisionId"] = revision.ToString(), ["instructions"] = "Wider", ["allowOverwrite"] = true }.ToString();
            foreach (var action in new[] { "approve", "request-changes", "complete", "artifact" })
            {
                Assert.AreEqual(401, routes.Handle(Request("agent/jobs/" + id + "/" + action, method: action == "artifact" ? "GET" : "POST", body: body)).Status);
                Assert.IsNull(forwardedPath);
            }
            Assert.AreEqual(400, routes.Handle(Request("agent/jobs/" + id + "/approve", fixture.Grant.SessionToken, body: "{}")).Status);
            Assert.IsNull(forwardedPath);
            Assert.AreEqual(202, routes.Handle(Request("agent/jobs/" + id + "/request-changes", fixture.Grant.SessionToken, body: body)).Status);
            Assert.AreEqual("jobs/" + id + "/request-changes", forwardedPath);
            var payload = JObject.Parse(forwardedBody);
            Assert.AreEqual(revision.ToString(), (string)payload["revisionId"]);
            Assert.AreEqual("Wider", (string)payload["instructions"]);
            Assert.IsNull(payload["allowOverwrite"]);
        }

        [TestMethod]
        public void AgentStatusRequiresAValidRemoteSessionBeforeProxying()
        {
            var fixture = new SessionFixture();
            var calls = 0;
            var routes = CreateRoutes(fixture, (method, path, body) =>
            {
                calls++;
                return new RemoteResponse { Status = 200, Body = "{\"executionMode\":\"Real\"}" };
            });

            var rejected = routes.Handle(Request("agent/status", method: "GET", body: ""));
            Assert.AreEqual(401, rejected.Status);
            Assert.AreEqual(0, calls);

            var accepted = routes.Handle(Request("agent/status", fixture.Grant.SessionToken, "GET", ""));
            Assert.AreEqual(200, accepted.Status);
            Assert.AreEqual("Real", (string)JObject.Parse(accepted.Body)["executionMode"]);
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void AgentJobCreationRejectsOversizedPromptsBeforeProxying()
        {
            var fixture = new SessionFixture();
            var calls = 0;
            var routes = CreateRoutes(fixture, (method, path, body) =>
            {
                calls++;
                return new RemoteResponse { Status = 201, Body = "{}" };
            });
            var prompt = new string('x', 2001);

            var response = routes.Handle(Request(
                "agent/jobs",
                fixture.Grant.SessionToken,
                "POST",
                new JObject { ["prompt"] = prompt }.ToString(Newtonsoft.Json.Formatting.None)));

            Assert.AreEqual(400, response.Status);
            Assert.AreEqual("prompt_invalid", (string)JObject.Parse(response.Body)["code"]);
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void AgentJobCreationForwardsOneBoundedImageOnlyOnJobRoute()
        {
            var fixture = new SessionFixture();
            string forwarded = null;
            var routes = CreateRoutes(fixture, (method, path, body) =>
            {
                Assert.AreEqual("jobs", path);
                forwarded = body;
                return new RemoteResponse { Status = 202, Body = "{}" };
            });
            var body = new JObject { ["prompt"] = "Create from image", ["image"] = new JObject
            {
                ["mediaType"] = "image/jpeg", ["dataBase64"] = new string('A', 100000), ["secret"] = "discard"
            } }.ToString(Newtonsoft.Json.Formatting.None);
            Assert.AreEqual(6 * 1024 * 1024, RemoteRequestPolicy.BodyLimitFor("/remote/v1/agent/jobs"));
            Assert.AreEqual(RemoteRequestPolicy.BodyLimit, RemoteRequestPolicy.BodyLimitFor("/remote/v1/input"));
            Assert.AreEqual(202, routes.Handle(Request("agent/jobs", fixture.Grant.SessionToken, body: body)).Status);
            var payload = JObject.Parse(forwarded);
            Assert.AreEqual(100000, ((string)payload["image"]["dataBase64"]).Length);
            Assert.IsNull(payload["image"]["secret"]);
            Assert.AreEqual(400, routes.Handle(Request("agent/jobs", fixture.Grant.SessionToken, body:
                "{\"prompt\":\"Create\",\"image\":{\"mediaType\":\"image/gif\",\"dataBase64\":\"AAAA\"}}")).Status);
        }

        [TestMethod]
        public void AgentJobCancelForwardsOnlyAValidatedJobIdentifier()
        {
            var fixture = new SessionFixture();
            string forwardedMethod = null, forwardedPath = null;
            var routes = CreateRoutes(fixture, (method, path, body) =>
            {
                forwardedMethod = method;
                forwardedPath = path;
                return new RemoteResponse { Status = 200, Body = "{\"state\":\"Cancelled\"}" };
            });

            var malformed = routes.Handle(Request("agent/jobs/not-a-guid/cancel", fixture.Grant.SessionToken));
            Assert.AreEqual(404, malformed.Status);
            Assert.IsNull(forwardedPath);

            var id = Guid.NewGuid();
            var response = routes.Handle(Request("agent/jobs/" + id.ToString("D") + "/cancel", fixture.Grant.SessionToken));
            Assert.AreEqual(200, response.Status);
            Assert.AreEqual("POST", forwardedMethod);
            Assert.AreEqual("jobs/" + id.ToString("D") + "/cancel", forwardedPath);
        }

        [TestMethod]
        public void SolidWorksStatusContractCarriesOnlyTheActiveDocumentTitle()
        {
            var property = typeof(SolidWorksSessionStatus).GetProperty("ActiveDocumentTitle");
            Assert.IsNotNull(property, "Remote status needs the current document title without exposing a COM document object.");
            Assert.AreEqual(typeof(string), property.PropertyType);
        }
    }
}
