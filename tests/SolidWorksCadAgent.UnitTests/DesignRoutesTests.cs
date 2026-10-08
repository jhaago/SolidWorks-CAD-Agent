using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Design;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.Contracts.Design;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesignRoutesTests
    {
        [TestMethod]
        public async Task DesignRoutes_CreateGetAndRejectPlanningBeforeBriefApproval()
        {
            var directory = Path.Combine(Path.GetTempPath(), "intake-routes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var repository = new DesignSessionRepository(Path.Combine(directory, "designs.db"));
                repository.Initialize();
                var store = new ReferenceImageStore(directory);
                var planned = false;
                var service = new DesignIntakeService(repository, store, new NeverCalledInterpreter(),
                    (prompt, token) =>
                    {
                        planned = true;
                        return Task.FromResult(Guid.NewGuid());
                    });
                var routes = new DesignRoutes(service, store);
                var created = await routes.HandleAsync(new AgentRequest("POST", "/designs", "{\"title\":\"Bracket\"}"), CancellationToken.None);
                Assert.AreEqual(201, created.StatusCode);
                var id = (string)JObject.Parse(created.JsonBody)["Id"];
                var get = await routes.HandleAsync(new AgentRequest("GET", "/designs/" + id, null), CancellationToken.None);
                Assert.AreEqual(200, get.StatusCode);
                var blocked = await routes.HandleAsync(new AgentRequest("POST", "/designs/" + id + "/plan-cad", "{\"revisionId\":\"" + Guid.NewGuid() + "\"}"), CancellationToken.None);
                Assert.AreEqual(409, blocked.StatusCode);
                Assert.IsFalse(planned);
                Assert.AreEqual(400, (await routes.HandleAsync(new AgentRequest("POST", "/designs/" + id + "/references", "{\"fileName\":\"part.png\",\"base64\":\"invalid\"}"), CancellationToken.None)).StatusCode);
            }
            finally
            {
                System.Data.SQLite.SQLiteConnection.ClearAllPools();
                Directory.Delete(directory, true);
            }
        }
        [TestMethod]
        public void ImageUploadEnvelope_IsBoundedOnlyForDesignReferencesAndJobs()
        {
            var path = "/designs/" + Guid.NewGuid() + "/references";
            Assert.IsNull(HostRequestPolicy.Validate("POST", "application/json", null, 2 * 1024 * 1024, true, path));
            Assert.IsNull(HostRequestPolicy.Validate("POST", "application/json", null, 2 * 1024 * 1024, true, "/jobs"));
            Assert.IsNull(HostRequestPolicy.Validate("POST", "application/json", null, 2 * 1024 * 1024, true, "/jobs/submit"));
            Assert.AreEqual(413, HostRequestPolicy.Validate("POST", "application/json", null, 2 * 1024 * 1024, true, "/settings").StatusCode);
            Assert.AreEqual(413, HostRequestPolicy.Validate("POST", "application/json", null, 7 * 1024 * 1024, true, "/jobs").StatusCode);
            Assert.AreEqual(413, HostRequestPolicy.Validate("POST", "application/json", null, 7 * 1024 * 1024, true, path).StatusCode);
            Assert.AreEqual(403, HostRequestPolicy.Validate("POST", "application/json", "http://untrusted", 100, true, path).StatusCode);
        }
        private sealed class NeverCalledInterpreter : IImageDesignInterpreter
        {
            public Task<DesignInterpretation> InterpretAsync(ImageDesignRequest request, CancellationToken token) => throw new InvalidOperationException("Unexpected interpretation");
        }
    }
}

