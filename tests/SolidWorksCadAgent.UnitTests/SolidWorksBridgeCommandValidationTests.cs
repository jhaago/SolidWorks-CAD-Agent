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
    public class SolidWorksBridgeCommandValidationTests
    {
        [TestMethod]
        public async Task Rectangle_NonPositiveWidth_IsRejectedBeforeCom()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.AddRectangle,
                    Parameters = JObject.FromObject(new
                    {
                        centerXmm = 0.0,
                        centerYmm = 0.0,
                        widthMm = 0.0,
                        heightMm = 60.0
                    })
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task Circle_NaNDiameter_IsRejectedBeforeCom()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.AddCircle,
                    Parameters = new JObject
                    {
                        ["centerXmm"] = 0.0,
                        ["centerYmm"] = 0.0,
                        ["diameterMm"] = double.NaN
                    }
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task CreateSketch_UnknownPlane_IsRejectedBeforeCom()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.CreateSketch,
                    Parameters = JObject.FromObject(new { plane = "Random Plane" })
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("UNSUPPORTED_PARAMETER_VALUE", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task CutExtrude_Blind_IsRejectedBeforeCom()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.CutExtrude,
                    Parameters = JObject.FromObject(new { endCondition = "Blind" })
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("UNSUPPORTED_PARAMETER_VALUE", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task SavePart_TraversalOutsideWorkspace_IsRejectedBeforeCom()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.SavePart,
                    Parameters = JObject.FromObject(new
                    {
                        path = @"..\escape.sldprt",
                        allowOverwrite = false
                    })
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("WORKSPACE_POLICY_VIOLATION", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task ArbitraryShellCommand_IsNotRegistered()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = "RunPowerShell",
                    Parameters = new JObject()
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("UNSUPPORTED_COMMAND", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        private sealed class RecordingSession : ISolidWorksSession
        {
            public int ApplicationInvocationCount { get; private set; }

            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken cancellationToken)
                => Task.FromResult(new SolidWorksSessionStatus());

            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken cancellationToken)
                => Task.FromResult(new SolidWorksSessionStatus());

            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken cancellationToken)
                => Task.FromResult(new SolidWorksSessionStatus());

            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken cancellationToken)
            {
                ApplicationInvocationCount++;
                return Task.FromResult(operation(new object()));
            }

            public void Dispose() { }
        }
    }
}
