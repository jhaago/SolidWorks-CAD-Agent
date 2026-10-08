using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public sealed class CadCommandRegistry
    {
        private readonly Dictionary<string, ICadCommandHandler> _handlers;
        private readonly Dictionary<string, IVersionedCadCommandHandler> _versionedHandlers;
        private readonly ReadOnlyCollection<string> _commandNames;

        public CadCommandRegistry(IEnumerable<ICadCommandHandler> handlers)
            : this(handlers, Array.Empty<IVersionedCadCommandHandler>())
        {
        }

        public CadCommandRegistry(IEnumerable<ICadCommandHandler> handlers,
            IEnumerable<IVersionedCadCommandHandler> versionedHandlers)
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

            _commandNames = Array.AsReadOnly(_handlers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());
            if (versionedHandlers == null) throw new ArgumentNullException(nameof(versionedHandlers));
            _versionedHandlers = new Dictionary<string, IVersionedCadCommandHandler>(StringComparer.Ordinal);
            foreach (var handler in versionedHandlers)
            {
                if (handler == null || string.IsNullOrWhiteSpace(handler.Name) || handler.OperationVersion <= 1)
                    throw new ArgumentException("Versioned handlers require a name and operation version greater than 1.", nameof(versionedHandlers));
                var key = VersionedKey(handler.Name, handler.OperationVersion);
                if (_versionedHandlers.ContainsKey(key))
                    throw new ArgumentException("Duplicate versioned CAD command handler: " + key, nameof(versionedHandlers));
                _versionedHandlers.Add(key, handler);
            }
        }

        public IReadOnlyCollection<string> CommandNames => _commandNames;

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

        public async Task<CadCommandResult> ExecuteVersionedAsync(
            CadVersionedCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
                return Failure("INVALID_EXECUTION_REQUEST", "Registry", "A versioned CAD execution request is required.", null);
            if (request.PlanVersion != 1 && request.PlanVersion != 2)
                return Failure("UNSUPPORTED_PLAN_VERSION", "Registry", "The CAD plan version is not supported for execution.", null);
            if (request.Command == null || string.IsNullOrWhiteSpace(request.Command.Command))
                return Failure("INVALID_COMMAND", "Registry", "A CAD command name is required.", null);

            if (request.PlanVersion == 1 && request.OperationVersion != 1)
                return Failure("INVALID_EXECUTION_REQUEST", "Registry", "Version-1 plans use version-1 operations.", null);
            if (request.PlanVersion == 2 && IsFeatureConsumer(request.Command.Command) && request.OperationVersion != 2)
                return Failure("INVALID_EXECUTION_REQUEST", "Registry", "Version-2 feature consumers require operation version 2.", null);
            if (request.ProfileSketchEntityId.HasValue &&
                (request.PlanVersion != 2 || request.OperationVersion != 2 ||
                 (request.Command.Command != CadCommandNames.Extrude && request.Command.Command != CadCommandNames.CutExtrude)))
                return Failure("INVALID_EXECUTION_CONTEXT", "Registry", "A profile-sketch reference is valid only for a version-2 feature operation.", null);
            if (request.PlanVersion == 2 && request.OperationVersion == 2 &&
                (request.Command.Command == CadCommandNames.Extrude || request.Command.Command == CadCommandNames.CutExtrude) &&
                (!request.Command.ManagedModelId.HasValue || request.Command.ManagedModelId.Value == Guid.Empty ||
                 !request.ProfileSketchEntityId.HasValue || request.ProfileSketchEntityId.Value == Guid.Empty))
                return Failure("MISSING_PROFILE_REFERENCE", "Registry", "A version-2 feature operation requires Host-managed model and profile-sketch IDs.", null);

            if (request.OperationVersion != 1)
            {
                if (!_versionedHandlers.TryGetValue(VersionedKey(request.Command.Command, request.OperationVersion), out var versioned))
                    return Failure("UNSUPPORTED_OPERATION_VERSION", "Registry", "No handler is registered for operation version " + request.OperationVersion + " of " + request.Command.Command + ".", null);
                CadError error;
                try
                {
                    error = versioned.Validate(request);
                }
                catch (Exception ex)
                {
                    return Failure("COMMAND_VALIDATION_FAILED", "Validate", "Versioned CAD command validation raised an exception.", ex.ToString());
                }
                if (error != null) return new CadCommandResult { Success = false, Data = new JObject(), Error = error };
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return await versioned.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    return Failure("COMMAND_EXECUTION_FAILED", "Execute", "Versioned CAD command execution failed; native mutation may be uncertain.", ex.ToString());
                }
            }
            if (request.ProfileSketchEntityId.HasValue)
                return Failure("INVALID_EXECUTION_CONTEXT", "Registry", "Version-1 handlers cannot consume profile-sketch references.", null);

            return await ExecuteAsync(request.Command, cancellationToken).ConfigureAwait(false);
        }

        private static string VersionedKey(string name, int version) => name + "\u001f" + version;

        private static bool IsFeatureConsumer(string command) =>
            command == CadCommandNames.Extrude || command == CadCommandNames.CutExtrude;

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
