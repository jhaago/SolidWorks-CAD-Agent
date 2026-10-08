using System.Threading;
using System.Threading.Tasks;

namespace SolidWorksCadAgent.Core.Ai
{
    public interface ICadPlanningProvider
    {
        Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Optional versioned-plan source. The Host parses and normalizes its untrusted JSON before persistence.</summary>
    public interface IVersionedCadPlanningProvider : ICadPlanningProvider
    {
        Task<string> PlanDocumentAsync(CadPlanningRequest request, CancellationToken cancellationToken);
    }

    /** Marker for planners that actually include attached images in their planning request. */
    public interface IImageCadPlanningProvider : ICadPlanningProvider { }
}
