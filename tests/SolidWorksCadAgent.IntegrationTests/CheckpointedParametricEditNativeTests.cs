using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass,DoNotParallelize,TestCategory("SolidWorksIntegration")]
    public class CheckpointedParametricEditNativeTests
    {
        [DataTestMethod,DataRow("width",150.0),DataRow("thickness",12.0),DataRow("holeDiameter",12.0),Timeout(240000)]
        public async Task ForkEditsExistingFeaturesAndRestoresExactCheckpoint(string role,double value)
        {
            if(Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1")Assert.Inconclusive("Native opt-in required.");
            var token=CancellationToken.None;
            var root=Path.Combine("C:/SolidWorks-CAD-Agent/Workspace/capability-tests","parametric-edit-recipe-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using(var repository=new SqliteJobRepository(Path.Combine(root,"jobs.sqlite")))
            using(var session=new SolidWorksSession())
            using(var bridge=new SolidWorksBridgeFacade(session,new WorkspacePolicy(root),repository)) {
                await repository.InitializeAsync();
                Assert.IsTrue((await session.AttachAsync(token)).IsConnected);
                var filename=Path.GetFileName(root)+"-source.sldprt";
                var coordinator=new JobCoordinator(repository,new NoPlanner(),bridge,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real});
                var sourceJob=await coordinator.CreateAndPlanAsync("V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as "+filename,token);
                var before=await repository.GetSnapshotAsync(sourceJob.Job.Id,token);
                await coordinator.ApproveAndExecuteAsync(sourceJob.Job.Id,before.Revisions.Single().Id,token);
                var saved=await repository.GetSnapshotAsync(sourceJob.Job.Id,token);
                Assert.AreEqual(JobState.ReadyForReview,saved.Job.State);
                var modelId=Guid.Parse(JObject.Parse(saved.Commands.First(c=>c.CommandName=="NewPart").ResultJson).Value<string>("modelId"));
                var source=await repository.GetModelAsync(modelId,token);
                var child=new CadModelIdentityRecord{ModelId=Guid.NewGuid(),ParentModelId=modelId,CurrentModelRevisionId=Guid.NewGuid(),
                    DocumentKind="Part",Status=CadModelIdentityStatus.Pending,ConfigurationKey=source.ConfigurationKey,CustomPropertyKey=source.CustomPropertyKey,
                    RegistryVersion=1,CreatedUtc=DateTime.UtcNow,UpdatedUtc=DateTime.UtcNow};
                await repository.RegisterModelAsync(child,token);
                var execution=Guid.NewGuid(); var id=child.ModelId.ToString("N");
                var metadata=new CadParametricEditSource{JobId=sourceJob.Job.Id,RevisionId=before.Revisions.Single().Id,ModelId=modelId,
                    ModelRevisionId=source.CurrentModelRevisionId,ConfigurationKey=source.ConfigurationKey,Path=filename,Sha256=source.LastSavedSha256,
                    SourcePlanJson=before.Revisions.Single().PlanJson,PlanSha256=CadParametricEditContract.HashText(before.Revisions.Single().PlanJson)};
                var edit=CadParametricEditContract.Build(metadata,role,value,"result-"+id+".sldprt");
                var editor=(ICheckpointedParametricEditExecutor)bridge;
                var checkpoint=await editor.ForkParametricPartAsync(new CadParametricEditNativeRequest{ExecutionId=execution,ChildModelId=child.ModelId,Source=metadata,
                    WorkingPath=".agent-edits/"+id+"-work.sldprt",CheckpointPath=".agent-edits/"+id+"-checkpoint.sldprt"},token);
                Assert.AreEqual(child.ModelId,checkpoint.ModelId);
                Assert.AreNotEqual(source.LastSavedSha256,checkpoint.Sha256,"Fork stamps a fresh model ID before checkpointing.");
                Assert.AreEqual(15,checkpoint.Bindings.Count);
                var applied=await editor.ApplyCheckpointedParameterAsync(execution,child.ModelId,edit,token);
                var observations=applied[role.StartsWith("hole")?"parametricHole":"parametricPlate"]["dimensions"];
                Assert.AreEqual(value,observations.Single(d=>d.Value<string>("role")==role).Value<double>("valueMm"),0.001);
                Assert.ThrowsException<InvalidOperationException>(()=>editor.ApplyCheckpointedParameterAsync(execution,child.ModelId,edit,token).GetAwaiter().GetResult(),"An edit cannot replay its mutation.");
                var restored=await editor.RestoreParametricCheckpointAsync(execution,child.ModelId,checkpoint.Sha256,token);
                Assert.AreEqual(role=="width"?120.0:role=="thickness"?10.0:8.0,
                    restored[role.StartsWith("hole")?"parametricHole":"parametricPlate"]["dimensions"].Single(d=>d.Value<string>("role")==role).Value<double>("valueMm"),0.001);
                Assert.AreEqual(source.LastSavedSha256,CheckpointedParametricEditorHash.HashFile(source.CanonicalPath));
                await bridge.ExecuteAsync(new CadCommandEnvelope{Command="CloseDocument",ExecutionId=execution,ManagedModelId=child.ModelId,Parameters=new JObject()},token);
            }
        }
        internal sealed class NoPlanner:IVersionedCadPlanningProvider {
            public Task<string> PlanDocumentAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit CAD plans must not use a provider.");
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit CAD plans must not use a provider.");
        }
    }
}
