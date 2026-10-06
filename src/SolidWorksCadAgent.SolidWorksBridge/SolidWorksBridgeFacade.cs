using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.SolidWorksBridge
{
    public sealed class SolidWorksBridgeFacade : ICadCommandExecutor, IDisposable
    {
        private readonly ISolidWorksSession _session;
        private readonly ISolidWorksSession _commandSession;
        private readonly bool _ownsSession;
        private readonly CadCommandRegistry _registry;
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public SolidWorksBridgeFacade()
            : this(new SolidWorksSession(), new WorkspacePolicy(new AgentSettings().WorkspaceRoot), true)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session)
            : this(session, new WorkspacePolicy(new AgentSettings().WorkspaceRoot), false)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session, WorkspacePolicy workspacePolicy)
            : this(session, workspacePolicy, false)
        {
        }

        private SolidWorksBridgeFacade(ISolidWorksSession session, WorkspacePolicy workspacePolicy, bool ownsSession)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            if (workspacePolicy == null)
            {
                throw new ArgumentNullException(nameof(workspacePolicy));
            }

            _ownsSession = ownsSession;
            _commandSession = new DocumentScopedSession(_session);
            _registry = new CadCommandRegistry(new ICadCommandHandler[]
            {
                new NewPartCommandHandler(_commandSession),
                new OpenPartCommandHandler(_commandSession, workspacePolicy),
                new SavePartCommandHandler(_commandSession, workspacePolicy),
                new CloseDocumentCommandHandler(_commandSession),
                new CreateSketchCommandHandler(_commandSession),
                new AddLineCommandHandler(_commandSession),
                new AddArcCommandHandler(_commandSession),
                new AddRectangleCommandHandler(_commandSession),
                new AddCircleCommandHandler(_commandSession),
                new AddSlotCommandHandler(_commandSession),
                new AddRegularPolygonCommandHandler(_commandSession),
                new ExitSketchCommandHandler(_commandSession),
                new ExtrudeCommandHandler(_commandSession),
                new CutExtrudeCommandHandler(_commandSession),
                new RebuildCommandHandler(_commandSession),
                new GetBodyCountCommandHandler(_commandSession),
                new GetBoundingBoxCommandHandler(_commandSession),
                new GetFeatureTreeCommandHandler(_commandSession),
                new GetRebuildErrorsCommandHandler(_commandSession)
            });
        }

        public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));
            }

            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (command != null && command.ExecutionId.HasValue)
                    await _session.InvokeWithApplicationAsync(application =>
                    {
                        SolidWorksCommandHandlerBase.DocumentContext(_commandSession).BeginExecution(command.ExecutionId);
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                return await _registry.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsSession)
            {
                _session.Dispose();
            }
        }

        // Separate document binding for each bridge, even when callers share a session.
        private sealed class DocumentScopedSession : ISolidWorksSession
        {
            private readonly ISolidWorksSession _inner;
            public DocumentScopedSession(ISolidWorksSession inner) => _inner = inner;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => _inner.GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => _inner.AttachAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => _inner.LaunchAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken token) =>
                _inner.InvokeWithApplicationAsync(operation, token);
            public void Dispose() { }
        }
    }
}
