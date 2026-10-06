using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class EdgeFinishingBridgeTests
    {
        [DataTestMethod]
        [DataRow("CreateSketchOnFace")]
        [DataRow("FilletEdges")]
        [DataRow("ChamferEdges")]
        public async Task IncompleteFaceCommandsAreNotRegistered(string name)
        {
            var session = new NeverInvokeSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = name, Parameters = new JObject() }, CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("UNSUPPORTED_COMMAND", result.Error.Code);
                Assert.AreEqual(0, session.Invocations);
            }
        }

        private sealed class NeverInvokeSession : ISolidWorksSession
        {
            public int Invocations;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken token) { Invocations++; throw new InvalidOperationException("Unsupported commands must not invoke SOLIDWORKS."); }
            public void Dispose() { }
        }
    }
}
