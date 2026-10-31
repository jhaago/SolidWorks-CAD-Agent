using System;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.References
{
    public interface IModelReferenceStore
    {
        Task RegisterModelAsync(CadModelIdentityRecord model, CancellationToken token);
        Task<CadModelIdentityRecord> GetModelAsync(Guid modelId, CancellationToken token);
        Task<CadModelIdentityRecord> FindModelByCanonicalPathAsync(string canonicalPath, CancellationToken token);
        Task UpdateModelAsync(CadModelIdentityRecord model, CancellationToken token);
        Task AddEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token);
        Task<CadEntityReferenceBinding> GetEntityBindingAsync(Guid modelId, Guid entityId, string configurationKey, CancellationToken token);
        Task UpdateEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token);
    }
}
