using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ReuseBridgeToolsTests
    {
        [TestMethod] public async Task ConstraintPreflightRunsThroughBridgeWithoutAnyComCall()
        {
            var session=new NeverInvokeSession();
            using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="ValidateSketchConstraintGraph",Parameters=JObject.Parse("{'entities':[{'id':'base','type':'line'}],'constraints':[{'type':'horizontal','entities':['base']}]} ")},CancellationToken.None);
                Assert.IsTrue(result.Success,result.Error?.Message);Assert.IsTrue(result.Data.Value<bool>("valid"));
                Assert.IsFalse(result.Data.Value<bool>("nativeSolved"));Assert.AreEqual(0,session.Invocations);
            }
        }
        [TestMethod] public async Task InvalidConstraintGraphReturnsStructuredDiagnosticsWithoutCom()
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="ValidateSketchConstraintGraph",Parameters=new JObject()},CancellationToken.None);
                Assert.IsFalse(result.Success);Assert.AreEqual("SKETCH_GRAPH_INVALID",result.Error.Code);
                Assert.IsTrue(((JArray)result.Data["errors"]).Count>0);Assert.AreEqual(0,session.Invocations);
            }
        }
        [DataTestMethod] [DataRow("{'kind':'Face','where':{'curve':'line'}}")] [DataRow("{'kind':'Edge','expect':'1'}")] [DataRow("{'kind':'Edge','where':{'radiusMm':-1}}")] [DataRow("{'kind':'Edge','expect':9223372036854775808}")]
        public async Task InvalidTopologyQueryIsRejectedBeforeCom(string json)
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="InspectTopology",Parameters=JObject.Parse(json)},CancellationToken.None);
                Assert.IsFalse(result.Success);Assert.AreEqual("INVALID_PARAMETERS",result.Error.Code);Assert.AreEqual(0,session.Invocations);
            }
        }
        [TestMethod] public void InspectionPortsDoNotExpandPlannerMutationWhitelist()
        {
            Assert.IsFalse(System.Linq.Enumerable.Contains(CadOperationCatalog.Names,"InspectTopology"));
            Assert.IsFalse(System.Linq.Enumerable.Contains(CadOperationCatalog.Names,"ValidateSketchConstraintGraph"));
            Assert.IsFalse(System.Linq.Enumerable.Contains(CadOperationCatalog.Names,"PlanComponentPattern"));
            Assert.IsFalse(System.Linq.Enumerable.Contains(CadOperationCatalog.Names,"PlanDrawingLayout"));
            Assert.IsFalse(System.Linq.Enumerable.Contains(CadOperationCatalog.Names,"InspectFaceBoundary"));
        }
        [DataTestMethod][DataRow("{}")][DataRow("{'normalAxis':'y','side':'Max'}")][DataRow("{'normalAxis':'Y','side':'Max','edge':1}")]
        public async Task InvalidFaceBoundarySelectorIsRejectedBeforeCom(string json)
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="InspectFaceBoundary",Parameters=JObject.Parse(json)},CancellationToken.None);
                Assert.IsFalse(result.Success);Assert.AreEqual("INVALID_PARAMETERS",result.Error.Code);Assert.AreEqual(0,session.Invocations);
            }
        }
        [DataTestMethod]
        [DataRow("PlanComponentPattern","{'pattern':'linear','seeds':[{'id':'bolt','originMm':[0,0,0]}],'direction':'x','spacingMm':20,'count':3}")]
        [DataRow("PlanDrawingLayout","{'sheetWidthMm':420,'sheetHeightMm':297,'modelSizeMm':[100,60,20],'views':['front','top']}")]
        public async Task FamilyPlanningIsRegisteredAndNeverInvokesCom(string command,string json)
        {
            var session=new NeverInvokeSession();using(var bridge=new SolidWorksBridgeFacade(session))
            {
                var result=await bridge.ExecuteAsync(new CadCommandEnvelope{Command=command,Parameters=JObject.Parse(json)},CancellationToken.None);
                Assert.IsTrue(result.Success,result.Error?.Message);Assert.IsTrue(result.Data.Value<bool>("planningOnly"));
                Assert.IsFalse(result.Data.Value<bool>("nativeCreated"));Assert.AreEqual(0,session.Invocations);
            }
        }
        private sealed class NeverInvokeSession:ISolidWorksSession
        {
            public int Invocations;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken t)=>Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken t)=>GetStatusAsync(t);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken t)=>GetStatusAsync(t);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object,T> operation,CancellationToken token){Invocations++;throw new InvalidOperationException("No COM invocation permitted.");}
            public void Dispose() { }
        }
    }
}
