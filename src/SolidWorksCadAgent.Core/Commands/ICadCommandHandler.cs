using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    public interface ICadCommandHandler
    {
        string Name { get; }
        CadError Validate(JObject parameters);
        Task<CadCommandResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken);
    }
}
