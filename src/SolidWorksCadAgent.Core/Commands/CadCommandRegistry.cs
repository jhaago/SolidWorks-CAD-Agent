using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public sealed class CadCommandRegistry
    {
        private readonly Dictionary<string, ICadCommandHandler> _handlers;

        public CadCommandRegistry(IEnumerable<ICadCommandHandler> handlers)
        {
            if (handlers == null)
            {
                throw new ArgumentNullException(nameof(handlers));
            }

            _handlers = new Dictionary<string, ICadCommandHandler>(StringComparer.Ordinal);
            foreach (var handler in handlers)
            {
                if (handler == null)
                {
                    throw new ArgumentException("Command handlers cannot contain null entries.", nameof(handlers));
                }

                if (string.IsNullOrWhiteSpace(handler.Name))
                {
                    throw new ArgumentException("Every CAD command handler must have a non-empty name.", nameof(handlers));
                }

                if (_handlers.ContainsKey(handler.Name))
                {
                    throw new ArgumentException("Duplicate CAD command handler name: " + handler.Name, nameof(handlers));
                }

                _handlers.Add(handler.Name, handler);
            }
        }

        public async Task<CadCommandResult> ExecuteAsync(
            CadCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (command == null || string.IsNullOrWhiteSpace(command.Command))
            {
                return Failure(
                    "INVALID_COMMAND",
                    "Registry",
                    "A CAD command name is required.",
                    null);
            }

            if (!_handlers.TryGetValue(command.Command, out var handler))
            {
                return Failure(
                    "UNSUPPORTED_COMMAND",
                    "Registry",
                    "CAD command is not registered: " + command.Command,
                    null);
            }

            var parameters = command.Parameters ?? new JObject();
            CadError validationError;
            try
            {
                validationError = handler.Validate(parameters);
            }
            catch (Exception ex)
            {
                return Failure(
                    "COMMAND_VALIDATION_FAILED",
                    "Validate",
                    "CAD command validation raised an exception.",
                    ex.ToString());
            }

            if (validationError != null)
            {
                return new CadCommandResult
                {
                    Success = false,
                    Data = new JObject(),
                    Error = validationError
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await handler.ExecuteAsync(parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(
                    "COMMAND_EXECUTION_FAILED",
                    "Execute",
                    "CAD command execution failed.",
                    ex.ToString());
            }
        }

        private static CadCommandResult Failure(string code, string stage, string message, string detail)
        {
            return new CadCommandResult
            {
                Success = false,
                Data = new JObject(),
                Error = new CadError
                {
                    Code = code,
                    Stage = stage,
                    Message = message,
                    Detail = detail
                }
            };
        }
    }
}
