using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class ParametricHolePersistenceTests
    {
        [TestMethod] public async Task FourRolesAndOutcomeCommitOnlyInSecondSketchNamespace()
        {
            await Fixture(async f=>{
                await f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"holeCircle",f.Model,f.Result,CancellationToken.None);
                foreach(var role in CadParametricIdentity.HoleRoles){var b=await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,role),"Default",CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Active,b.Status);Assert.AreEqual(f.Attempt.ProspectiveModelRevisionId,b.CreatedAtModelRevisionId);
                    Assert.IsNull(await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.PlateSketch,role),"Default",CancellationToken.None));}
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,(await f.Repository.GetV2MutationAttemptAsync(f.Attempt.Id,CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"holeCircle",f.Model,CancellationToken.None));
            });
        }
        [TestMethod] public async Task ForeignIncompleteWrongNamesAndValuesCannotCreatePartialBindings()
        {
            await Fixture(async f=>{
                var original=(JObject)f.Result.Data.DeepClone();
                Action<JObject>[] corrupt={d=>d["modelId"]=Guid.NewGuid().ToString(),d=>d["sketchEntityId"]=f.PlateSketch.ToString(),
                    d=>((JArray)d["roles"]).RemoveAt(0),d=>((JArray)d["roles"]).Add(d["roles"][0].DeepClone()),
                    d=>d["roles"][1]["nativeDimensionFullName"]="WrongName@Sketch2@Part1",d=>d["dimensions"][0]["isDriving"]=false,
                    d=>d["dimensions"][1]["valueMm"]=21,d=>d["dimensions"][2]["ownerEntityId"]=f.PlateSketch.ToString(),
                    d=>d["fullyDefined"]=false,d=>d["hasFixedContourEntities"]=true,d=>d["contourEdgeCount"]=2};
                foreach(var change in corrupt){f.Result.Data=(JObject)original.DeepClone();change((JObject)f.Result.Data["parametricCapture"]);
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"holeCircle",f.Model,f.Result,CancellationToken.None));
                    foreach(var role in CadParametricIdentity.HoleRoles)Assert.IsNull(await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,role),"Default",CancellationToken.None));}
            });
        }
        [TestMethod] public async Task GenericCommitCannotBypassNativeHoleCapture()
        {
            await Fixture(async f=>{
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2SequentialOutcomeAsync(f.Job,f.Revision,"holeCircle",f.Model,f.Result,CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,(await f.Repository.GetV2MutationAttemptAsync(f.Attempt.Id,CancellationToken.None)).Status);
            });
        }
        [TestMethod] public async Task FailureRollsBackAllFourBindingsAndUncertaintyBlocksReplay()
        {
            await Fixture(async f=>{
                using(var c=new System.Data.SQLite.SQLiteConnection("Data Source="+f.Path+";Version=3;")){c.Open();using(var cmd=c.CreateCommand()){
                    cmd.CommandText="CREATE TRIGGER FailHole BEFORE UPDATE OF Status ON V2MutationAttempts WHEN NEW.StepKey='holeCircle' AND NEW.Status='Applied' BEGIN SELECT RAISE(ABORT,'forced'); END;";cmd.ExecuteNonQuery();}}
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"holeCircle",f.Model,f.Result,CancellationToken.None));
                foreach(var role in CadParametricIdentity.HoleRoles)Assert.IsNull(await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,role),"Default",CancellationToken.None));
                await f.Repository.MarkV2MutationAttemptUncertainAsync(f.Attempt.Id,CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"holeCircle",f.Model,CancellationToken.None));
            });
        }
        private sealed class Data{public SqliteJobRepository Repository;public string Path;public Guid Job,Revision,Model,Sketch,PlateSketch;public CadV2MutationAttemptRecord Attempt;public CadCommandResult Result;}
        private static async Task Fixture(Func<Data,Task> action)
        {
            var path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hole-store-"+Guid.NewGuid()+".db");
            try{using(var repository=new SqliteJobRepository(path)){
                await repository.InitializeAsync();var plan=CadParametricHoleContractTests.Hole();var now=DateTime.UtcNow;
                var f=new Data{Repository=repository,Path=path,Job=Guid.NewGuid(),Revision=Guid.NewGuid(),Model=Guid.NewGuid(),Sketch=plan.Steps[5].OutputEntityId.Value,PlateSketch=plan.Steps[1].OutputEntityId.Value};
                await repository.CreateAsync(new CadJob{Id=f.Job,Prompt="hole",State=JobState.Executing,PlanValidated=true,CreatedUtc=now,UpdatedUtc=now});
                await repository.AppendRevisionAsync(new JobRevision{Id=f.Revision,JobId=f.Job,RevisionNumber=1,Prompt="hole",PlanJson=JsonConvert.SerializeObject(plan),CreatedUtc=now});
                await repository.PrepareV2NewPartAttemptAsync(f.Job,f.Revision,"new",new CadModelIdentityRecord{ModelId=f.Model,DocumentKind="Part",Status=CadModelIdentityStatus.Pending,
                    CustomPropertyKey="SolidWorksCadAgent.ModelId",CurrentModelRevisionId=Guid.NewGuid(),ConfigurationKey="Default",RegistryVersion=1,CreatedUtc=now,UpdatedUtc=now},CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(f.Job,f.Revision,"new",f.Model,CadCommandResult.Ok(new{modelId=f.Model.ToString("D"),documentTitle="Part1",configurationKey="Default"}),CancellationToken.None);
                for(int index=1;index<=5;index++){
                    var step=plan.Steps[index];await repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,step.StepKey,f.Model,CancellationToken.None);
                    if(step.Command=="CreateSketch"){
                        var binding=await repository.GetEntityBindingAsync(f.Model,step.OutputEntityId.Value,"Default",CancellationToken.None);binding.NativeReferenceBytes=new byte[]{1,2,3};binding.Status=CadEntityReferenceStatus.Active;
                        await repository.UpdateEntityBindingAsync(binding,CancellationToken.None);await repository.CommitV2SequentialOutcomeAsync(f.Job,f.Revision,step.StepKey,f.Model,CadCommandResult.Ok(new{entityId=step.OutputEntityId.Value.ToString("D")}),CancellationToken.None);
                    }else if(index==2||index==4){var capture=Capture(f.Model,f.PlateSketch,index==2?new[]{"bottom","right","top","left","centreDiagonal","width","length"}:new[]{"boss","thickness"});
                        var data=(JObject)step.Parameters.DeepClone();data["parametricCapture"]=JObject.FromObject(capture);if(index==4)data["featureName"]="Boss1";
                        await repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,step.StepKey,f.Model,new CadCommandResult{Success=true,Data=data},CancellationToken.None);
                    }else await repository.CommitV2SequentialOutcomeAsync(f.Job,f.Revision,step.StepKey,f.Model,CadCommandResult.Ok(new{exited=true}),CancellationToken.None);
                }
                f.Attempt=await repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"holeCircle",f.Model,CancellationToken.None);
                f.Result=CadCommandResult.Ok(new{centerXmm=20,centerYmm=10,diameterMm=8,parametricCapture=Capture(f.Model,f.Sketch,CadParametricIdentity.HoleRoles.ToArray())});await action(f);
            }}finally{foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        }
        private static CadParametricSketchObservation Capture(Guid model,Guid sketch,string[] roles)
        {
            bool hole=roles.Contains("holeCircle");var capture=new CadParametricSketchObservation{EvidenceVersion=1,ModelId=model,SketchEntityId=sketch,ConfigurationKey="Default",FullyDefined=true,RequiredRelationsPresent=true,
                ContourEdgeCount=hole?1:4,ConstructionEdgeCount=hole?0:1,Geometry=JObject.Parse("{centerXmm:20,centerYmm:10,diameterMm:8}")};
            foreach(var role in roles){bool dim=new[]{"width","length","thickness","holeDiameter","holeX","holeY"}.Contains(role);string name=role=="width"?"AgentWidth":role=="length"?"AgentLength":role=="thickness"?"AgentThickness":role=="holeDiameter"?"AgentHoleDiameter":role=="holeX"?"AgentHoleX":"AgentHoleY";
                capture.Roles.Add(new CadParametricRoleCapture{EntityId=CadParametricIdentity.For(model,sketch,role),Role=role,OwnerKind=dim?"DimensionOwnerFeature":role=="boss"?"Feature":"SketchSegment",OwnerReferenceBytes=new byte[]{4,5,6},NativeDimensionFullName=dim?name+"@Sketch@Part1":null});
                if(dim)capture.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(model,sketch,role),Role=role,OwnerEntityId=role=="thickness"?CadParametricIdentity.For(model,sketch,"boss"):sketch,
                    ValueMm=role=="width"?120:role=="length"?60:role=="holeDiameter"?8:role=="holeX"?20:10,IsDriving=true});}
            return capture;
        }
    }
}
