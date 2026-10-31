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
        [DataTestMethod][DataRow("FilletEdges")][DataRow("ChamferEdges")]
        public async Task InvalidFinishingParametersAreRejectedBeforeCom(string command)
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command=command,Parameters=new JObject()},CancellationToken.None);
                Assert.IsFalse(result.Success);Assert.AreEqual("INVALID_PARAMETERS",result.Error.Code);Assert.AreEqual(0,session.Invocations);
            }
        }
        [TestMethod] public async Task FinishingInspectionRejectsSelectionParametersBeforeCom()
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="GetEdgeFinishingFeatures",Parameters=JObject.FromObject(new{featureName="Fillet1"})},CancellationToken.None);
                Assert.IsFalse(result.Success);Assert.AreEqual("INVALID_PARAMETERS",result.Error.Code);Assert.AreEqual(0,session.Invocations);
            }
        }
        [TestMethod] public void FinishingPortsAreRegisteredWithoutExpandingThePlanner()
        {
            using(var bridge=new SolidWorksBridgeFacade(new NeverInvokeSession()))
            {
                // Native availability deliberately stays false in compile-only builds; this checks handler registration.
                Assert.IsTrue(System.Linq.Enumerable.Contains(bridge.RegisteredCommandNames,"FilletEdges"));Assert.IsTrue(System.Linq.Enumerable.Contains(bridge.RegisteredCommandNames,"ChamferEdges"));
                Assert.IsTrue(System.Linq.Enumerable.Contains(bridge.RegisteredCommandNames,"GetEdgeFinishingFeatures"));Assert.IsFalse(System.Linq.Enumerable.Contains(bridge.RegisteredCommandNames,"CreateSketchOnFace"));
            }
            Assert.IsFalse(System.Linq.Enumerable.Contains(SolidWorksCadAgent.Core.Commands.CadOperationCatalog.Names,"FilletEdges"));
            Assert.IsFalse(System.Linq.Enumerable.Contains(SolidWorksCadAgent.Core.Commands.CadOperationCatalog.Names,"ChamferEdges"));
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
