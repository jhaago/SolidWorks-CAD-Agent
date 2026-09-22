using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.SolidWorksBridge
{
    public sealed class SolidWorksBridgeFacade : IDisposable
    {
        private readonly ISolidWorksSession _session;
        private readonly bool _ownsSession;
        private readonly CadCommandRegistry _registry;
        private bool _disposed;

        public SolidWorksBridgeFacade()
            : this(new SolidWorksSession(), true)
        {
        }

        public SolidWorksBridgeFacade(ISolidWorksSession session)
            : this(session, false)
        {
        }

        private SolidWorksBridgeFacade(ISolidWorksSession session, bool ownsSession)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ownsSession = ownsSession;
            _registry = new CadCommandRegistry(new ICadCommandHandler[]
            {
                new NewPartCommandHandler(_session),
                new CreateSketchCommandHandler(_session),
                new AddRectangleCommandHandler(_session),
                new AddCircleCommandHandler(_session),
                new ExitSketchCommandHandler(_session),
                new ExtrudeCommandHandler(_session),
                new CutExtrudeCommandHandler(_session),
                new RebuildCommandHandler(_session)
            });
        }

        public Task<CadCommandResult> ExecuteAsync(
            CadCommandEnvelope command,
            CancellationToken cancellationToken)
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
