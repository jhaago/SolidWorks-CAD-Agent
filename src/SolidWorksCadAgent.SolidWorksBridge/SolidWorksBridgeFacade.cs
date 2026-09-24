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
        private readonly bool _ownsSession;
        private readonly CadCommandRegistry _registry;
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
            _registry = new CadCommandRegistry(new ICadCommandHandler[]
            {
                new NewPartCommandHandler(_session),
                new OpenPartCommandHandler(_session, workspacePolicy),
                new SavePartCommandHandler(_session, workspacePolicy),
                new CloseDocumentCommandHandler(_session),
                new CreateSketchCommandHandler(_session),
                new AddRectangleCommandHandler(_session),
                new AddCircleCommandHandler(_session),
                new ExitSketchCommandHandler(_session),
                new ExtrudeCommandHandler(_session),
                new CutExtrudeCommandHandler(_session),
                new RebuildCommandHandler(_session),
                new GetBodyCountCommandHandler(_session),
                new GetBoundingBoxCommandHandler(_session),
                new GetFeatureTreeCommandHandler(_session),
                new GetRebuildErrorsCommandHandler(_session)
            });
        }

        public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SolidWorksBridgeFacade));
            }

            return _registry.ExecuteAsync(command, cancellationToken);
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
    }
}
