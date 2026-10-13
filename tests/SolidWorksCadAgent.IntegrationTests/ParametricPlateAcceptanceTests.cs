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
    public class ParametricPlateAcceptanceTests
    {
        [TestMethod,Timeout(240000)]
        public async Task ApprovedAsymmetricPlateHasDrivingParametersAfterReopen()
        {
            if(System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1")Assert.Inconclusive("Native opt-in required.");
#if SOLIDWORKS_INTEROP
            var root=Path.GetFullPath(Path.Combine("C:/SolidWorks-CAD-Agent/Workspace/capability-tests","parametric-"+Guid.NewGuid().ToString("N")));
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
                    var coordinator=new JobCoordinator(repository,new FixedPlanner(),bridge,new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real});
                    var routes=new AgentRoutes(repository,session,coordinator);
                    var created=await routes.HandleAsync(new AgentRequest("POST","/jobs",new JObject{["prompt"]="V2 parametric plate: 120 x 60 x 10 mm; save as "+filename}.ToString()),CancellationToken.None);
                    Assert.AreEqual(201,created.StatusCode,created.JsonBody);
                    var id=Guid.Parse(JObject.Parse(created.JsonBody).Value<string>("id"));
                    jobId=id;
                    var planned=await repository.GetSnapshotAsync(id,CancellationToken.None);Assert.AreEqual(JobState.AwaitingApproval,planned.Job.State);
                    var approved=await routes.HandleAsync(new AgentRequest("POST","/jobs/"+id+"/approve",new JObject{["revisionId"]=planned.Revisions.Single().Id.ToString("D")}.ToString()),CancellationToken.None);
                    var snapshot=await repository.GetSnapshotAsync(id,CancellationToken.None);
                    var first=snapshot.Commands.FirstOrDefault(c=>c.CommandName=="NewPart");
                    if(first!=null)createdTitle=JObject.Parse(first.ResultJson).Value<string>("documentTitle");
                    Assert.AreEqual(200,approved.StatusCode,approved.JsonBody);
                    Assert.AreEqual(JobState.ReadyForReview,snapshot.Job.State,approved.JsonBody);
                    Assert.IsTrue(snapshot.Verifications.Any(v=>v.CheckName=="V2SavedParametricPlate"&&v.Passed));
                    var check=snapshot.Verifications.Last(v=>v.CheckName=="V2SavedParametricPlate");
                    var actual=JObject.Parse(check.ActualJson)["observed"]["parametricPlate"];
                    Assert.IsTrue(actual.Value<bool>("fullyDefined"));
                    Assert.AreEqual(3,((JArray)actual["dimensions"]).Count);
                    Assert.IsTrue(File.Exists(Path.Combine(root,filename)));
                    Console.WriteLine("Retained native parametric artifact: "+Path.Combine(root,filename));
                    var artifact=await routes.HandleAsync(new AgentRequest("GET","/jobs/"+id+"/artifact",null),CancellationToken.None);
                    Assert.AreEqual(200,artifact.StatusCode,artifact.JsonBody);
                    var modelId=Guid.Parse(JObject.Parse(first.ResultJson).Value<string>("modelId"));
                    var modelIdentity=await repository.GetModelAsync(modelId,CancellationToken.None);
                    var parsed=SolidWorksCadAgent.Core.Commands.CadPlanDocumentReader.ReadPersisted(planned.Revisions.Single().PlanJson);
                    var sketchId=parsed.CandidateV2.Steps[1].OutputEntityId.Value;
                    var bindings=new List<CadEntityReferenceBinding>{await repository.GetEntityBindingAsync(modelId,sketchId,modelIdentity.ConfigurationKey,CancellationToken.None)};
                    foreach(var role in CadParametricIdentity.Roles)bindings.Add(await repository.GetEntityBindingAsync(modelId,CadParametricIdentity.For(modelId,sketchId,role),modelIdentity.ConfigurationKey,CancellationToken.None));
                    var sourcePath=Path.Combine(root,filename);var sourceHash=Hash(sourcePath);
                    foreach(var role in new[]{"width","thickness","missingWidth"})
                    {
                        var copyPath=Path.Combine(root,Path.GetFileNameWithoutExtension(filename)+"-"+role+".sldprt");
                        using(var input=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                        using(var output=new FileStream(copyPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))input.CopyTo(output);
                        await session.InvokeWithApplicationAsync(a=>
                        {
                            var app=(SldWorks)a;int errors=0,warnings=0;
                            var copy=(ModelDoc2)app.OpenDoc6(copyPath,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref errors,ref warnings);
                            Assert.IsNotNull(copy);Assert.AreEqual(0,errors);
                            try
                            {
                                var roleName=role=="missingWidth"?"width":role;
                                var binding=bindings.Single(b=>b.EntityId==CadParametricIdentity.For(modelId,sketchId,roleName));
                                int nativeStatus;var owner=(Feature)copy.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes,out nativeStatus);Assert.AreEqual(0,nativeStatus);
                                var localName=JObject.Parse(binding.SemanticFingerprintJson).Value<string>("nativeDimensionName");
                                Dimension dimension=null;
                                for(var display=(DisplayDimension)owner.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)owner.GetNextDisplayDimension(display))
                                {var d=(Dimension)display.GetDimension2(0);if(d.FullName.Split('@')[0]==localName)dimension=d;}
                                Assert.IsNotNull(dimension);
                                if(role=="missingWidth")
                                {
                                    dimension.Name="UntrackedWidth";
                                    Assert.ThrowsException<InvalidOperationException>(()=>ParametricPlateInspector.Inspect(copy,modelIdentity,sketchId,bindings));
                                }
                                else
                                {
                                    Assert.AreEqual(0,dimension.SetSystemValue3(role=="width"?0.150:0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                                    Assert.IsTrue(copy.ForceRebuild3(false));
                                    var observation=ParametricPlateInspector.Inspect(copy,modelIdentity,sketchId,bindings);
                                    Assert.IsTrue(observation.FullyDefined);Assert.AreEqual(role=="width"?150.0:12.0,observation.Dimensions.Single(d=>d.Role==role).ValueMm,0.001);
                                }
                                var bodies=(object[])((PartDoc)copy).GetBodies2((int)swBodyType_e.swSolidBody,false);Assert.AreEqual(1,bodies.Length);
                                var body=(Body2)bodies[0];var mass=(double[])body.GetMassProperties(1);
                                Assert.AreEqual(role=="width"?90000.0:role=="thickness"?86400.0:72000.0,mass[3]*1e9,0.01);
                                double x,y,z;body.GetExtremePoint(1,0,0,out x,out y,out z);Assert.AreEqual(role=="width"?0.075:0.060,x,0.000001);
                                body.GetExtremePoint(0,1,0,out x,out y,out z);Assert.AreEqual(role=="thickness"?0.012:0.010,y,0.000001);
                            }
                            finally
                            {
                                app.CloseDoc(copy.GetTitle());int activationError=0;
                                app.ActivateDoc3(Path.GetFileName(sourcePath),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activationError);
                            }
                            return true;
                        },CancellationToken.None);
                        Assert.AreEqual(sourceHash,Hash(sourcePath),"Owned-copy parameter tests must preserve the published source.");
                    }
                    // A completion record alone cannot replace saved parameter evidence.
                    await repository.AppendVerificationAsync(new VerificationResultRecord{Id=Guid.NewGuid(),JobId=id,RevisionNumber=1,CheckName="V2SavedParametricPlate",Passed=false,ExpectedJson=check.ExpectedJson,ActualJson="{}",CreatedUtc=DateTime.UtcNow});
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
            public Task<string> PlanDocumentAsync(CadPlanningRequest request,CancellationToken token)
            {
                var plan=JObject.Parse(CadV2RectangleBossPrompt.BuildCandidate("V2 rectangle boss: 120 x 60 x 10 mm; save as plate.sldprt"));
                plan["steps"][2]["operationVersion"]=2;plan["steps"][4]["operationVersion"]=3;return Task.FromResult(plan.ToString());
            }
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("No cloud/legacy planning permitted in native fixture.");
        }
        private static string Hash(string path)
        {using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
