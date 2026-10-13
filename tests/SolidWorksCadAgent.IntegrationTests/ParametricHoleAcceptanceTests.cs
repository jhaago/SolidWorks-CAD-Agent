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
        public Task ApprovedHoleHasAllSixDrivingParametersAfterReopen(bool square,bool corruptReadback)=>BuildAndVerify(square,corruptReadback,false);
        [TestMethod,Timeout(240000)] public Task MinimumEdgeClearanceHoleCompletesNativeSaveAndDownload()=>BuildAndVerify(false,false,true);
        private async Task BuildAndVerify(bool square,bool corruptReadback,bool nearEdge)
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
                    var coordinator=new JobCoordinator(repository,new FixedPlanner(),executor,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real});
                    var routes=new AgentRoutes(repository,session,coordinator);
                    var length=square?120.0:60.0;
                    var created=await routes.HandleAsync(new AgentRequest("POST","/jobs",new JObject{["prompt"]="V2 parametric plate with hole: 120 x "+(square?120:60)+" x 10 mm; hole diameter "+(square?20:8)+" mm at x="+(nearEdge?"55.999":"20")+" mm, y="+(nearEdge?"25.999":square?"20":"10")+" mm; save as "+filename}.ToString()),CancellationToken.None);
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
                    var modelId=Guid.Parse(JObject.Parse(first.ResultJson).Value<string>("modelId"));
                    if(!nearEdge)await CheckSavedCopies(repository,session,bridge,id,modelId,planned.Revisions.Single().PlanJson,root,filename,square);
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
            public Task<string> PlanDocumentAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit native hole intake must not call a provider.");
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("No cloud/legacy planning permitted in native fixture.");
        }
#if SOLIDWORKS_INTEROP
        private static async Task CheckSavedCopies(SqliteJobRepository repository,SolidWorksSession session,SolidWorksBridgeFacade bridge,Guid job,Guid modelId,string planJson,string root,string filename,bool square)
        {
            var token=CancellationToken.None;var identity=await repository.GetModelAsync(modelId,token);var plan=CadPlanDocumentReader.ReadPersisted(planJson).CandidateV2;
            var sketch=plan.Steps[5].OutputEntityId.Value;var plateSketch=plan.Steps[1].OutputEntityId.Value;
            var bindings=new List<CadEntityReferenceBinding>{await repository.GetEntityBindingAsync(modelId,sketch,identity.ConfigurationKey,token)};
            foreach(var role in CadParametricIdentity.HoleRoles)bindings.Add(await repository.GetEntityBindingAsync(modelId,CadParametricIdentity.For(modelId,sketch,role),identity.ConfigurationKey,token));
            var plateBindings=new List<CadEntityReferenceBinding>{await repository.GetEntityBindingAsync(modelId,plateSketch,identity.ConfigurationKey,token)};
            foreach(var role in CadParametricIdentity.Roles)plateBindings.Add(await repository.GetEntityBindingAsync(modelId,CadParametricIdentity.For(modelId,plateSketch,role),identity.ConfigurationKey,token));
            var source=Path.Combine(root,filename);var hash=Hash(source);string missingCopy=null;
            foreach(var scenario in square?new[]{"swapAxes","swapDiameter","missing"}:new[]{"diameter","position","missing"})
            {
                var copyPath=Path.Combine(root,Path.GetFileNameWithoutExtension(filename)+"-"+scenario+".sldprt");
                if(scenario=="missing")missingCopy=Path.GetFileName(copyPath);
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var output=new FileStream(copyPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))input.CopyTo(output);
                await session.InvokeWithApplicationAsync(a=>
                {
                    var app=(SldWorks)a;int errors=0,warnings=0;
                    var copy=(ModelDoc2)app.OpenDoc6(copyPath,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref errors,ref warnings);
                    Assert.IsNotNull(copy);Assert.AreEqual(0,errors);
                    try
                    {
                        int status;var owner=(Feature)copy.Extension.GetObjectByPersistReference3(bindings.Single(b=>b.EntityId==CadParametricIdentity.For(modelId,sketch,"holeX")).NativeReferenceBytes,out status);
                        Assert.AreEqual(0,status);var x=(Dimension)copy.Parameter("AgentHoleX@"+owner.Name);
                        var y=(Dimension)copy.Parameter("AgentHoleY@"+owner.Name);var diameter=(Dimension)copy.Parameter("AgentHoleDiameter@"+owner.Name);
                        Assert.IsNotNull(x);Assert.IsNotNull(y);Assert.IsNotNull(diameter);
                        if(scenario=="swapAxes"||scenario=="swapDiameter")
                        {
                            var other=scenario=="swapAxes"?y:diameter;var otherName=scenario=="swapAxes"?"AgentHoleY":"AgentHoleDiameter";
                            x.Name="TemporaryHoleX";other.Name="AgentHoleX";x.Name=otherName;
                            Assert.ThrowsException<InvalidOperationException>(()=>ParametricHoleInspector.Inspect(copy,identity,sketch,bindings),"Equal values cannot prove native parameter direction/type.");
                        }
                        else if(scenario=="missing")
                        {
                            x.Name="UntrackedHoleX";
                            Assert.ThrowsException<InvalidOperationException>(()=>ParametricHoleInspector.Inspect(copy,identity,sketch,bindings));
                        }
                        else
                        {
                            if(scenario=="diameter")Assert.AreEqual(0,diameter.SetSystemValue3(0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                            else{Assert.AreEqual(0,x.SetSystemValue3(0.025,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));Assert.AreEqual(0,y.SetSystemValue3(0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));}
                            Assert.IsTrue(copy.ForceRebuild3(false));
                            var observation=ParametricHoleInspector.Inspect(copy,identity,sketch,bindings);
                            Assert.IsTrue(observation.FullyDefined);Assert.AreEqual(scenario=="diameter"?12.0:8.0,observation.Geometry.Value<double>("diameterMm"),0.001);
                            Assert.AreEqual(scenario=="position"?25.0:20.0,observation.Geometry.Value<double>("centerXmm"),0.001);
                            Assert.AreEqual(scenario=="position"?12.0:10.0,observation.Geometry.Value<double>("centerYmm"),0.001);
                        }
                        var plate=ParametricPlateInspector.Inspect(copy,identity,plateSketch,plateBindings);
                        Assert.AreEqual(0,CadParametricPlateVerifier.Check(CadParametricPlateContract.ReadIntent(plan),plate).Count);
                        var bodies=(object[])((PartDoc)copy).GetBodies2((int)swBodyType_e.swSolidBody,false);Assert.AreEqual(1,bodies.Length);
                        var mass=(double[])((Body2)bodies[0]).GetMassProperties(1);var d=scenario=="diameter"?12.0:square?20.0:8.0;
                        Assert.AreEqual(120.0*(square?120:60)*10-Math.PI*d*d/4*10,mass[3]*1e9,0.01);
                    }
                    finally{app.CloseDoc(copy.GetTitle());int activationError=0;app.ActivateDoc3(filename,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activationError);}
                    return true;
                },token);
                Assert.AreEqual(hash,Hash(source),"Copy probes must preserve immutable saved source.");
            }
            // Raw native copy probes bypass managed collision guards. Restore the exact saved fixture.
            var closed=await bridge.ExecuteAsync(new CadCommandEnvelope{Command=CadCommandNames.CloseDocument,Parameters=new JObject(),ExecutionId=job,ManagedModelId=modelId},token);
            Assert.IsTrue(closed.Success,closed.Error?.Message);Assert.AreEqual(hash,Hash(source));
            var reopened=await bridge.ExecuteAsync(new CadCommandEnvelope{Command=CadCommandNames.OpenPart,Parameters=JObject.FromObject(new{path=filename}),ExecutionId=job},token);
            Assert.IsTrue(reopened.Success,reopened.Error?.Message);
            var collision=await bridge.ExecuteAsync(new CadCommandEnvelope{Command=CadCommandNames.OpenPart,Parameters=JObject.FromObject(new{path=missingCopy}),ExecutionId=job},token);
            Assert.IsFalse(collision.Success);Assert.AreEqual("MODEL_ID_COLLISION",collision.Error.Code);
            var verified=await new CadParametricHoleVerifier(repository,bridge).VerifyAsync(job,(await repository.GetSnapshotAsync(job,token)).Revisions.Single().Id,modelId,token);
            Assert.IsTrue(verified.Passed,verified.Reason);
        }

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

