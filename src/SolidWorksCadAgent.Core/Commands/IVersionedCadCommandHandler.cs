using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    // Explicit operation-version dispatch. Version-1 handlers remain on ICadCommandHandler.
    public interface IVersionedCadCommandHandler
    {
        string Name { get; }
        int OperationVersion { get; }
        CadError Validate(CadVersionedCommandRequest request);
        Task<CadCommandResult> ExecuteAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken);
    }
}
