using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public interface ICadCommandExecutor
    {
        Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken);
    }
}
