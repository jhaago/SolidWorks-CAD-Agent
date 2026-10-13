using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;
using System.Collections.Generic;
using System.Security.Cryptography;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif
namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass,DoNotParallelize,TestCategory("SolidWorksIntegration")]
    public class ParametricHoleAcceptanceTests
    {
        [DataTestMethod,DataRow(false,false),DataRow(true,false),DataRow(false,true),Timeout(240000)]
        public async Task ApprovedHoleHasAllSixDrivingParametersAfterReopen(bool square,bool corruptReadback)
        {
            if(System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1")Assert.Inconclusive("Native opt-in required.");
#if SOLIDWORKS_INTEROP
            var root=Path.GetFullPath(Path.Combine("C:/SolidWorks-CAD-Agent/Workspace/capability-tests","parametric-hole-"+Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var filename=Path.GetFileName(root)+".sldprt";
            using(var repository=new SqliteJobRepository(Path.Combine(root,"jobs.sqlite")))
            using(var session=new SolidWorksSession())
            using(var bridge=new SolidWorksBridgeFacade(session,new WorkspacePolicy(root),repository))
            {
                await repository.InitializeAsync();var status=await session.AttachAsync(CancellationToken.None);Assert.IsTrue(status.IsConnected,status.ErrorMessage);
                var original=await session.InvokeWithApplicationAsync(a=>((SldWorks)a).IActiveDoc2?.GetTitle(),CancellationToken.None);
                string createdTitle=null;
                Guid jobId=Guid.Empty;
                try
                {
                    var executor=new ReadbackFaultExecutor(bridge,repository,session,corruptReadback);
                    var coordinator=new JobCoordinator(repository,new FixedPlanner(filename,square),executor,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real});
                    var routes=new AgentRoutes(repository,session,coordinator);
                    var length=square?120.0:60.0;
                    var created=await routes.HandleAsync(new AgentRequest("POST","/jobs",new JObject{["prompt"]="Native dimensioned hole fixture"}.ToString()),CancellationToken.None);
                    Assert.AreEqual(201,created.StatusCode,created.JsonBody);
                    var id=Guid.Parse(JObject.Parse(created.JsonBody).Value<string>("id"));
                    jobId=id;
                    var planned=await repository.GetSnapshotAsync(id,CancellationToken.None);Assert.AreEqual(JobState.AwaitingApproval,planned.Job.State);
                    var approved=await routes.HandleAsync(new AgentRequest("POST","/jobs/"+id+"/approve",new JObject{["revisionId"]=planned.Revisions.Single().Id.ToString("D")}.ToString()),CancellationToken.None);
                    var snapshot=await repository.GetSnapshotAsync(id,CancellationToken.None);
                    var first=snapshot.Commands.FirstOrDefault(c=>c.CommandName=="NewPart");
                    if(first!=null)createdTitle=JObject.Parse(first.ResultJson).Value<string>("documentTitle");
                    Assert.AreEqual(200,approved.StatusCode,approved.JsonBody);
                    if(corruptReadback)
                    {
                        Assert.IsTrue(executor.Injected,"Fault must occur after the managed reopen.");
                        Assert.AreEqual(JobState.Failed,snapshot.Job.State,approved.JsonBody);
                        Assert.IsTrue(snapshot.Verifications.Any(v=>v.CheckName=="V2SavedParametricHole"&&!v.Passed));
                        var denied=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+id+"/artifact",null),CancellationToken.None);
                        Assert.AreEqual(409,denied.StatusCode,denied.JsonBody);
                        return;
                    }
                    Assert.AreEqual(JobState.ReadyForReview,snapshot.Job.State,approved.JsonBody);
                    Assert.IsTrue(snapshot.Verifications.Any(v=>v.CheckName=="V2SavedParametricHole"&&v.Passed));
                    var check=snapshot.Verifications.Last(v=>v.CheckName=="V2SavedParametricHole");
                    var actual=JObject.Parse(check.ActualJson)["observed"]["parametricHole"];
                    Assert.IsTrue(actual.Value<bool>("fullyDefined"));
                    Assert.AreEqual(3,((JArray)actual["dimensions"]).Count);
                    Assert.IsTrue(snapshot.Verifications.Any(v=>v.CheckName=="V2SavedParametricPlate"&&v.Passed));
                    Assert.IsTrue(File.Exists(Path.Combine(root,filename)));
                    Console.WriteLine("Retained native parametric artifact: "+Path.Combine(root,filename));
                    var artifact=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+id+"/artifact",null),CancellationToken.None);
                    Assert.AreEqual(200,artifact.StatusCode,artifact.JsonBody);
                    // A completion record alone cannot replace saved parameter evidence.
                    await repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=id,RevisionNumber=1,CheckName="V2SavedParametricHole",Passed=false,ExpectedJson=check.ExpectedJson,ActualJson="{}",CreatedUtc=DateTime.UtcNow});
                    artifact=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+id+"/artifact",null),CancellationToken.None);
                    Assert.AreEqual(409,artifact.StatusCode,artifact.JsonBody);
                }
                finally
                {
                    if(createdTitle==null&&jobId!=Guid.Empty)
                    {
                        var failedSnapshot=await repository.GetSnapshotAsync(jobId,CancellationToken.None);
                        var first=failedSnapshot?.Commands.FirstOrDefault(c=>c.CommandName=="NewPart"&&c.Success);
                        if(first!=null)createdTitle=JObject.Parse(first.ResultJson).Value<string>("documentTitle");
                    }
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var app=(SldWorks)a;var doc=app.IActiveDoc2;
                        if(doc!=null&&doc.GetTitle()!=original&&(doc.GetTitle()==createdTitle||string.Equals(doc.GetPathName(),Path.Combine(root,filename),StringComparison.OrdinalIgnoreCase)))app.CloseDoc(doc.GetTitle());
                        if(original!=null){int errors=0;app.ActivateDoc3(original,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref errors);}
                        return true;
                    },CancellationToken.None);
                }
            }
#else
            Assert.Inconclusive("Native interop required.");
#endif
        }
        private sealed class FixedPlanner:IVersionedCadPlanningProvider
        {
            private readonly string filename;private readonly bool square;
            public FixedPlanner(string filename,bool square){this.filename=filename;this.square=square;}
            public Task<string> PlanDocumentAsync(CadPlanningRequest request,CancellationToken token)=>Task.FromResult(
                CadParametricHolePrompt.BuildCandidate("V2 parametric plate with hole: 120 x "+(square?120:60)+" x 10 mm; hole diameter "+(square?20:8)+" mm at x=20 mm, y="+(square?20:10)+" mm; save as "+filename));
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("No cloud/legacy planning permitted in native fixture.");
        }
#if SOLIDWORKS_INTEROP
        // Test-only boundary: alter one owned fixture after real managed reopen,
        // then let the production inspector, recorder and coordinator reject it.
        private sealed class ReadbackFaultExecutor:ICadCommandExecutor,IVersionedCadCommandExecutor,ICadOperationAvailability
        {
            private readonly SolidWorksBridgeFacade bridge;private readonly SqliteJobRepository repository;private readonly SolidWorksSession session;private readonly bool enabled;
            private bool reopened;public bool Injected{get;private set;}
            public ReadbackFaultExecutor(SolidWorksBridgeFacade bridge,SqliteJobRepository repository,SolidWorksSession session,bool enabled)
            {this.bridge=bridge;this.repository=repository;this.session=session;this.enabled=enabled;}
            public bool SupportsManagedReferences=>bridge.SupportsManagedReferences;
            public bool IsOperationRegistered(string name,int version)=>bridge.IsOperationRegistered(name,version);
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request,CancellationToken token)=>bridge.ExecuteVersionedAsync(request,token);
            public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command,CancellationToken token)
            {
                if(enabled&&reopened&&!Injected&&command.Command=="InspectParametricHole")
                {
                    var model=await repository.GetModelAsync(command.ManagedModelId.Value,token);
                    var sketch=Guid.Parse(command.Parameters.Value<string>("sketchEntityId"));
                    var binding=await repository.GetEntityBindingAsync(model.ModelId,CadParametricIdentity.For(model.ModelId,sketch,"holeX"),model.ConfigurationKey,token);
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var doc=((SldWorks)a).IActiveDoc2;Assert.AreEqual(model.CanonicalPath,doc.GetPathName(),true);
                        int status;var owner=(Feature)doc.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes,out status);Assert.AreEqual(0,status);
                        var dimension=(Dimension)doc.Parameter("AgentHoleX@"+owner.Name);Assert.IsNotNull(dimension);
                        dimension.Name="UntrackedHoleX";
                        var mass=(double[])((Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody,false))[0]).GetMassProperties(1);
                        Assert.AreEqual(72000.0-Math.PI*16*10,mass[3]*1e9,0.01,"Fault must preserve valid physical geometry.");
                        return true;
                    },token);
                    Injected=true;
                }
                var result=await bridge.ExecuteAsync(command,token);
                if(command.Command==CadCommandNames.OpenPart&&result.Success)reopened=true;
                return result;
            }
        }
#endif
        private static string Hash(string path)
        {using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}

