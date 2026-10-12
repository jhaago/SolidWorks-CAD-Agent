using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadCommandRegistryTests
    {
        [TestMethod]
        public async Task ExecuteAsync_UnregisteredCommand_ReturnsUnsupportedCommand()
        {
            var registry = new CadCommandRegistry(Array.Empty<ICadCommandHandler>());
            var result = await registry.ExecuteAsync(
                new CadCommandEnvelope { Command = "RunPowerShell", Parameters = new JObject() },
                CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.IsNotNull(result.Error);
            Assert.AreEqual("UNSUPPORTED_COMMAND", result.Error.Code);
        }

        [TestMethod]
        public async Task ExecuteAsync_ValidationFails_DoesNotExecuteHandler()
        {
            var handler = new FakeHandler("Extrude")
            {
                ValidationError = new CadError
                {
                    Code = "INVALID_PARAMETERS",
                    Stage = "Validate",
                    Message = "depth_mm is required"
                }
            };
            var registry = new CadCommandRegistry(new[] { handler });

            var result = await registry.ExecuteAsync(
                new CadCommandEnvelope { Command = "Extrude", Parameters = new JObject() },
                CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("INVALID_PARAMETERS", result.Error.Code);
            Assert.IsFalse(handler.ExecuteCalled);
        }

        [TestMethod]
        public void Constructor_DuplicateCommandNames_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new CadCommandRegistry(new ICadCommandHandler[]
                {
                    new FakeHandler("Extrude"),
                    new FakeHandler("Extrude")
                }));
        }

        [TestMethod]
        public async Task ExecuteAsync_HandlerThrows_ReturnsStructuredExecutionError()
        {
            var handler = new FakeHandler("Rebuild")
            {
                ExceptionToThrow = new InvalidOperationException("COM failure")
            };
            var registry = new CadCommandRegistry(new[] { handler });

            var result = await registry.ExecuteAsync(
                new CadCommandEnvelope { Command = "Rebuild", Parameters = new JObject() },
                CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("COMMAND_EXECUTION_FAILED", result.Error.Code);
            Assert.AreEqual("Execute", result.Error.Stage);
        }

        [TestMethod]
        public async Task ExecuteAsync_AlreadyCancelled_DoesNotExecuteHandler()
        {
            var handler = new FakeHandler("Rebuild");
            var registry = new CadCommandRegistry(new[] { handler });
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    registry.ExecuteAsync(
                        new CadCommandEnvelope { Command = "Rebuild", Parameters = new JObject() },
                        cts.Token));
            }

            Assert.IsFalse(handler.ExecuteCalled);
        }

        [TestMethod]
        public async Task ExecuteVersionedAsync_V2ExtrudeDoesNotFallThroughToV1Handler()
        {
            var handler = new FakeHandler("Extrude");
            var registry = new CadCommandRegistry(new[] { handler });
            var request = new CadVersionedCommandRequest(2, 2,
                new CadCommandEnvelope
                {
                    Command = "Extrude",
                    Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                    ManagedModelId = Guid.Parse("11111111-1111-4111-8111-111111111111")
                },
                Guid.Parse("22222222-2222-4222-8222-222222222222"));
            Assert.AreEqual("{}", Newtonsoft.Json.JsonConvert.SerializeObject(request));

            var missingReference = await registry.ExecuteVersionedAsync(
                new CadVersionedCommandRequest(2, 2,
                    new CadCommandEnvelope { Command = "Extrude", Parameters = JObject.FromObject(new { depthMm = 5.0 }) }),
                CancellationToken.None);
            Assert.IsFalse(missingReference.Success);
            Assert.AreEqual("MISSING_PROFILE_REFERENCE", missingReference.Error.Code);
            Assert.IsFalse(handler.ExecuteCalled);

            var legacyVersion = await registry.ExecuteVersionedAsync(
                new CadVersionedCommandRequest(2, 1,
                    new CadCommandEnvelope { Command = "Extrude", Parameters = JObject.FromObject(new { depthMm = 5.0 }) }),
                CancellationToken.None);
            Assert.IsFalse(legacyVersion.Success);
            Assert.AreEqual("INVALID_EXECUTION_REQUEST", legacyVersion.Error.Code);
            Assert.IsFalse(handler.ExecuteCalled);

            var result = await registry.ExecuteVersionedAsync(request, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual("UNSUPPORTED_OPERATION_VERSION", result.Error.Code);
            Assert.IsFalse(handler.ExecuteCalled);

            var legacy = await registry.ExecuteAsync(
                new CadCommandEnvelope { Command = "Extrude", Parameters = JObject.FromObject(new { depthMm = 5.0 }) },
                CancellationToken.None);
            Assert.IsTrue(legacy.Success);
            Assert.IsTrue(handler.ExecuteCalled);
        }

        [TestMethod]
        public async Task ExecuteVersionedAsync_UsesOnlyRegisteredVersionTwoHandler()
        {
            var legacy = new FakeHandler(CadCommandNames.Extrude);
            var versionTwo = new FakeVersionedHandler(CadCommandNames.Extrude, 2);
            var registry = new CadCommandRegistry(new[] { legacy }, new[] { versionTwo });
            var request = new CadVersionedCommandRequest(2, 2,
                new CadCommandEnvelope
                {
                    Command = CadCommandNames.Extrude,
                    Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                    ManagedModelId = Guid.NewGuid()
                }, Guid.NewGuid());

            versionTwo.ValidationError = new CadError { Code = "INVALID_PARAMETERS", Stage = "Validate", Message = "Invalid depth." };
            var invalid = await registry.ExecuteVersionedAsync(request, CancellationToken.None);
            Assert.AreEqual("INVALID_PARAMETERS", invalid.Error.Code);
            Assert.IsFalse(versionTwo.ExecuteCalled);
            Assert.IsFalse(legacy.ExecuteCalled);

            versionTwo.ValidationError = null;
            var accepted = await registry.ExecuteVersionedAsync(request, CancellationToken.None);
            Assert.IsTrue(accepted.Success);
            Assert.IsTrue(versionTwo.ExecuteCalled);
            Assert.IsFalse(legacy.ExecuteCalled);

            var cut = await registry.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                new CadCommandEnvelope { Command = CadCommandNames.CutExtrude, Parameters = new JObject(), ManagedModelId = Guid.NewGuid() },
                Guid.NewGuid()), CancellationToken.None);
            Assert.AreEqual("UNSUPPORTED_OPERATION_VERSION", cut.Error.Code);
        }

        [TestMethod]
        public void Constructor_DuplicateVersionedHandler_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() => new CadCommandRegistry(
                Array.Empty<ICadCommandHandler>(), new IVersionedCadCommandHandler[]
                {
                    new FakeVersionedHandler(CadCommandNames.Extrude, 2),
                    new FakeVersionedHandler(CadCommandNames.Extrude, 2)
                }));
        }

        [TestMethod]
        public async Task ExecuteVersionedAsync_ValidationExceptionDoesNotEnterNativeHandler()
        {
            var handler = new FakeVersionedHandler(CadCommandNames.Extrude, 2)
            {
                ValidationException = new InvalidOperationException("Invalid versioned command metadata.")
            };
            var registry = new CadCommandRegistry(Array.Empty<ICadCommandHandler>(), new[] { handler });
            var result = await registry.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                new CadCommandEnvelope
                {
                    Command = CadCommandNames.Extrude, Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                    ManagedModelId = Guid.NewGuid(), ExecutionId = Guid.NewGuid()
                }, Guid.NewGuid()), CancellationToken.None);
            Assert.AreEqual("COMMAND_VALIDATION_FAILED", result.Error.Code);
            Assert.AreEqual("Validate", result.Error.Stage);
            Assert.IsFalse(handler.ExecuteCalled);
        }

        private sealed class FakeVersionedHandler : IVersionedCadCommandHandler
        {
            public FakeVersionedHandler(string name, int operationVersion)
            {
                Name = name;
                OperationVersion = operationVersion;
            }

            public string Name { get; }
            public int OperationVersion { get; }
            public CadError ValidationError { get; set; }
            public Exception ValidationException { get; set; }
            public bool ExecuteCalled { get; private set; }
            public CadError Validate(CadVersionedCommandRequest request)
            {
                if (ValidationException != null) throw ValidationException;
                return ValidationError;
            }
            public Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken)
            {
                ExecuteCalled = true;
                return Task.FromResult(CadCommandResult.Ok(new { executed = true }));
            }
        }

        private sealed class FakeHandler : ICadCommandHandler
        {
            public FakeHandler(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public CadError ValidationError { get; set; }
            public Exception ExceptionToThrow { get; set; }
            public bool ExecuteCalled { get; private set; }

            public CadError Validate(JObject parameters)
            {
                return ValidationError;
            }

            public Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
            {
                ExecuteCalled = true;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return Task.FromResult(CadCommandResult.Ok(new { executed = true }));
            }
        }
    }
}
