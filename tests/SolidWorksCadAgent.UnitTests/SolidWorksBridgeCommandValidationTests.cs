using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
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
        public async Task CutExtrude_BlindWithoutDepth_IsRejectedBeforeCom()
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
                Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
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

        [TestMethod]
        public async Task ManagedIdentityMetadata_WithoutReferenceStore_IsRejectedBeforeSta()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.NewPart,
                    Parameters = new JObject(),
                    ManagedModelId = Guid.NewGuid()
                }, CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("MODEL_REFERENCE_STORE_UNAVAILABLE", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task VersionTwoExtrude_RequiresManagedReferenceStoreBeforeSta()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Extrude,
                        Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                        ManagedModelId = Guid.NewGuid(), ExecutionId = Guid.NewGuid()
                    },
                    Guid.NewGuid()), CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("MODEL_REFERENCE_STORE_UNAVAILABLE", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);

                var noExecution = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Extrude,
                        Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                        ManagedModelId = Guid.NewGuid()
                    }, Guid.NewGuid()), CancellationToken.None);
                Assert.IsFalse(noExecution.Success);
                Assert.AreEqual("MISSING_EXECUTION_ID", noExecution.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);

                var malformed = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Extrude,
                        Parameters = JObject.FromObject(new { depthMm = -5.0 }),
                        ManagedModelId = Guid.NewGuid()
                    }, Guid.NewGuid()), CancellationToken.None);
                Assert.IsFalse(malformed.Success);
                Assert.AreEqual("INVALID_PARAMETERS", malformed.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);

                var legacyVersion = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 1,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Extrude,
                        Parameters = JObject.FromObject(new { depthMm = 5.0 })
                    }), CancellationToken.None);
                Assert.IsFalse(legacyVersion.Success);
                Assert.AreEqual("INVALID_EXECUTION_REQUEST", legacyVersion.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task UnsupportedPlanVersion_IsNotLoweredToLegacyEnvelope()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(99, 1,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Extrude,
                        Parameters = JObject.FromObject(new { depthMm = 5.0 })
                    }), CancellationToken.None);

                Assert.IsFalse(result.Success);
                Assert.AreEqual("UNSUPPORTED_PLAN_VERSION", result.Error.Code);
                Assert.AreEqual(0, session.ApplicationInvocationCount);
            }
        }

        [TestMethod]
        public async Task VersionTwoCut_RemainsUnregisteredAndCannotUseLegacyHandler()
        {
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                CadVersionedCommandRequest Request(object parameters) => new CadVersionedCommandRequest(2, 2,
                    new CadCommandEnvelope
                    {
                        Command = CadCommandNames.CutExtrude,
                        Parameters = JObject.FromObject(parameters), ManagedModelId = Guid.NewGuid()
                    }, Guid.NewGuid());

                var valid = await bridge.ExecuteVersionedAsync(Request(new { endCondition = "ThroughAll" }), CancellationToken.None);
                Assert.IsFalse(valid.Success);
                Assert.AreEqual("UNSUPPORTED_OPERATION_VERSION", valid.Error.Code);
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
