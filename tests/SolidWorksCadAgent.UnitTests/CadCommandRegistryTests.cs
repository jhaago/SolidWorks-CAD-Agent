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
