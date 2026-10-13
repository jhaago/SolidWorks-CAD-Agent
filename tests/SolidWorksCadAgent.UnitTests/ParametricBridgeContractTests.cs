using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class ParametricBridgeContractTests
    {
        [TestMethod] public async Task NewVersionedOperationsRejectMissingTrustedContextBeforeCom()
        {
            using(var bridge=new SolidWorksBridgeFacade(new NeverSession()))
            {
                foreach(var item in new[]{new{Name="AddRectangle",Version=2,Json="{centerXmm:0,centerYmm:0,widthMm:120,heightMm:60}"},new{Name="Extrude",Version=3,Json="{depthMm:10}"}})
                {
                    var result=await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2,item.Version,new CadCommandEnvelope {Command=item.Name,Parameters=JObject.Parse(item.Json)}),CancellationToken.None);
                    Assert.IsFalse(result.Success);Assert.IsTrue(result.Error.Code=="INVALID_EXECUTION_CONTEXT"||result.Error.Code=="MISSING_PROFILE_REFERENCE",result.Error.Code);
                }
            }
        }
        private sealed class NeverSession:ISolidWorksSession
        {
            public void Dispose(){}
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken t)=>throw new AssertFailedException("COM forbidden");
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken t)=>throw new AssertFailedException("COM forbidden");
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken t)=>throw new AssertFailedException("COM forbidden");
            public Task<T> InvokeWithApplicationAsync<T>(Func<object,T> f,CancellationToken t)=>throw new AssertFailedException("COM forbidden");
        }
    }
}
