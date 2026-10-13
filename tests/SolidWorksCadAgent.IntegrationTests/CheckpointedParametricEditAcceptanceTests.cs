using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass,DoNotParallelize,TestCategory("SolidWorksIntegration")]
    public class CheckpointedParametricEditAcceptanceTests
    {
        [DataTestMethod,DataRow(false,"width",150.0,false),DataRow(true,"holeX",25.0,false),DataRow(true,"width",150.0,true),Timeout(240000)]
        public async Task ApprovedEditPublishesVerifiedChildOrRestoresCheckpoint(bool hole,string role,double value,bool badReadback)
        {
            if(Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1")Assert.Inconclusive("Native opt-in required.");
            var token=CancellationToken.None;
            var root=Path.Combine("C:/SolidWorks-CAD-Agent/Workspace/capability-tests","parametric-edit-acceptance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            using(var repository=new SqliteJobRepository(Path.Combine(root,"jobs.sqlite")))
            using(var session=new SolidWorksSession())
            using(var bridge=new SolidWorksBridgeFacade(session,new WorkspacePolicy(root),repository)) {
                await repository.InitializeAsync();Assert.IsTrue((await session.AttachAsync(token)).IsConnected);
                var filename=Path.GetFileName(root)+"-source.sldprt";
                var sourceCoordinator=new JobCoordinator(repository,new CheckpointedParametricEditNativeTests.NoPlanner(),bridge,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real});
                var source=await sourceCoordinator.CreateAndPlanAsync(hole?
                    "V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as "+filename:
                    "V2 parametric plate: 120 x 60 x 10 mm; save as "+filename,token);
                source=await sourceCoordinator.ApproveAndExecuteAsync(source.Job.Id,source.Revisions.Single().Id,token);
                Assert.AreEqual(JobState.ReadyForReview,source.Job.State);
                var sourceHash=CheckpointedParametricEditorHash.HashFile(Path.Combine(root,filename));
                var fault=new SavedFaultExecutor(bridge,badReadback);
                var coordinator=new JobCoordinator(repository,new CheckpointedParametricEditNativeTests.NoPlanner(),fault,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real,AutoMode=true});
                var routes=new AgentRoutes(repository,session,coordinator);
                var output=Path.GetFileName(root)+"-result.sldprt";
                var edit=await coordinator.CreateAndPlanAsync("V2 parametric edit: job "+source.Job.Id+"; parameter "+role+" = "+value.ToString(System.Globalization.CultureInfo.InvariantCulture)+" mm; save as "+output,token);
                Assert.AreEqual(JobState.AwaitingApproval,edit.Job.State,"AutoMode must still require current explicit edit approval.");
                Assert.AreEqual(0,edit.Commands.Count);
                edit=await coordinator.ApproveAndExecuteAsync(edit.Job.Id,edit.Revisions.Single().Id,token);
                var attempt=await repository.GetParametricEditAttemptAsync(edit.Job.Id,edit.Revisions.Single().Id,token);
                Assert.AreEqual(sourceHash,CheckpointedParametricEditorHash.HashFile(Path.Combine(root,filename)));
                var artifact=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+edit.Job.Id+"/artifact",null),token);
                if(badReadback) {
                    Assert.IsTrue(fault.Injected);Assert.AreEqual(JobState.Failed,edit.Job.State);
                    Assert.AreEqual(CadParametricEditAttemptStatus.Restored,attempt.Status);
                    Assert.AreEqual(409,artifact.StatusCode,artifact.JsonBody);
                    var child=await repository.GetModelAsync(attempt.ModelId,token);
                    Assert.AreEqual(attempt.CheckpointSha256,child.LastSavedSha256);
                    var observed=await fault.RestoreObservation;
                    Assert.AreEqual(120.0,observed["parametricPlate"]["dimensions"].Single(d=>d.Value<string>("role")=="width").Value<double>("valueMm"),0.001);
                }else {
                    Assert.AreEqual(JobState.ReadyForReview,edit.Job.State);Assert.AreEqual(CadParametricEditAttemptStatus.Published,attempt.Status);
                    Assert.AreEqual(200,artifact.StatusCode,artifact.JsonBody);
                    var child=await repository.GetModelAsync(attempt.ModelId,token);
                    Assert.AreEqual(CadPlanDocumentReader.ReadPersisted(edit.Revisions.Single().PlanJson).CandidateV2.ParametricEdit.ModelId,child.ParentModelId);
                    Assert.AreEqual(attempt.SavedSha256,CheckpointedParametricEditorHash.HashFile(child.CanonicalPath));
                    var pending=await coordinator.CreateAndPlanAsync("V2 parametric edit: job "+source.Job.Id+"; parameter width = 160 mm; save as "+Path.GetFileName(root)+"-stale.sldprt",token);
                    var sourceEvidence=source.Verifications.Last(v=>v.CheckName==CadV2SavedVerificationRecorder.CheckName);
                    await repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=source.Job.Id,RevisionNumber=1,CheckName=sourceEvidence.CheckName,Passed=false,ExpectedJson=sourceEvidence.ExpectedJson,ActualJson="{}",CreatedUtc=DateTime.UtcNow});
                    var stale=await Assert.ThrowsExceptionAsync<JobCoordinatorException>(()=>coordinator.ApproveAndExecuteAsync(pending.Job.Id,pending.Revisions.Single().Id,token));
                    Assert.AreEqual("PARAMETRIC_EDIT_SOURCE_UNAVAILABLE",stale.Code);
                    Assert.IsNull(await repository.GetParametricEditAttemptAsync(pending.Job.Id,pending.Revisions.Single().Id,token));
                    Assert.AreEqual(0,(await repository.GetSnapshotAsync(pending.Job.Id,token)).Commands.Count);
                    var latest=edit.Verifications.Last(v=>v.CheckName==CadV2SavedVerificationRecorder.CheckName);
                    await repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=edit.Job.Id,RevisionNumber=1,CheckName=latest.CheckName,Passed=false,ExpectedJson=latest.ExpectedJson,ActualJson="{}",CreatedUtc=DateTime.UtcNow});
                    artifact=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+edit.Job.Id+"/artifact",null),token);Assert.AreEqual(409,artifact.StatusCode);
                }
                await bridge.ExecuteAsync(new CadCommandEnvelope{Command="CloseDocument",ExecutionId=edit.Job.Id,ManagedModelId=attempt.ModelId,Parameters=new JObject()},token);
            }
        }
        private sealed class SavedFaultExecutor:ICadCommandExecutor,IVersionedCadCommandExecutor,ICadOperationAvailability,ICheckpointedParametricEditExecutor
        {
            private readonly SolidWorksBridgeFacade _inner;private readonly bool _fault;private bool _savedReopened;
            public bool Injected;public Task<JObject> RestoreObservation=Task.FromResult<JObject>(null);
            public SavedFaultExecutor(SolidWorksBridgeFacade inner,bool fault){_inner=inner;_fault=fault;}
            public bool SupportsManagedReferences=>_inner.SupportsManagedReferences;
            public bool SupportsCheckpointedParametricEditing=>_inner.SupportsCheckpointedParametricEditing;
            public bool IsOperationRegistered(string name,int version)=>_inner.IsOperationRegistered(name,version);
            public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command,CancellationToken token){var result=await _inner.ExecuteAsync(command,token);if(command.Command=="OpenPart"&&result.Success)_savedReopened=true;return result;}
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request,CancellationToken token)=>_inner.ExecuteVersionedAsync(request,token);
            public Task<CadParametricEditCheckpoint> ForkParametricPartAsync(CadParametricEditNativeRequest request,CancellationToken token)=>_inner.ForkParametricPartAsync(request,token);
            public Task<JObject> ApplyCheckpointedParameterAsync(Guid job,Guid model,CadPlanV2Document plan,CancellationToken token)=>_inner.ApplyCheckpointedParameterAsync(job,model,plan,token);
            public async Task<JObject> InspectCheckpointedPartAsync(Guid job,Guid model,CadPlanV2Document plan,CancellationToken token){var observed=await _inner.InspectCheckpointedPartAsync(job,model,plan,token);if(_fault&&_savedReopened&&!Injected){observed["parametricPlate"]["fullyDefined"]=false;Injected=true;}return observed;}
            public Task<JObject> RestoreParametricCheckpointAsync(Guid job,Guid model,string hash,CancellationToken token)=>RestoreObservation=_inner.RestoreParametricCheckpointAsync(job,model,hash,token);
        }
    }
}
