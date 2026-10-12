using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>
    /// Execution-only boundary for Host-normalized versioned plan steps.
    /// The legacy ICadCommandExecutor contract remains unchanged.
    /// </summary>
    public interface IVersionedCadCommandExecutor
    {
        Task<CadCommandResult> ExecuteVersionedAsync(
            CadVersionedCommandRequest request,
            CancellationToken cancellationToken);
    }
}
