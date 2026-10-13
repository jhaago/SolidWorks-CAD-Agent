using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.Core.References
{
    public interface ICheckpointedParametricEditExecutor
    {
        bool SupportsCheckpointedParametricEditing {get;}
        Task<CadParametricEditCheckpoint> ForkParametricPartAsync(CadParametricEditNativeRequest request,CancellationToken token);
        Task<JObject> ApplyCheckpointedParameterAsync(Guid executionId,Guid childModelId,CadPlanV2Document approvedPlan,CancellationToken token);
        Task<JObject> InspectCheckpointedPartAsync(Guid executionId,Guid childModelId,CadPlanV2Document expectedPart,CancellationToken token);
        Task<JObject> RestoreParametricCheckpointAsync(Guid executionId,Guid childModelId,string checkpointSha256,CancellationToken token);
    }
    public sealed class CadParametricEditNativeRequest
    {
        public Guid ExecutionId {get;set;}
        public Guid ChildModelId {get;set;}
        public CadParametricEditSource Source {get;set;}
        public string WorkingPath {get;set;}
        public string CheckpointPath {get;set;}
    }
    public sealed class CadParametricEditCheckpoint
    {
        public Guid ModelId {get;set;}
        public string WorkingPath {get;set;}
        public string CheckpointPath {get;set;}
        public string Sha256 {get;set;}
        public IReadOnlyList<CadEntityReferenceBinding> Bindings {get;set;}
        public JObject Observed {get;set;}
    }
    public static class CheckpointedParametricEditorHash
    {
        public static string HashFile(string path)
        {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
        }
    }
}
