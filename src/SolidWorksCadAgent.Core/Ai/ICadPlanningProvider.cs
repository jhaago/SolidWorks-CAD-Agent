using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.Core.Ai
{
    public interface ICadPlanningProvider
    {
        Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken cancellationToken);
    }
}
