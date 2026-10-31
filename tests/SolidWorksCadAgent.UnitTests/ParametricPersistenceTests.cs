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
using SolidWorksCadAgent.Core.References;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class ParametricPersistenceTests
    {
        [TestMethod] public async Task OutcomeAndRoleBindingsCommitTogether()
        {
            await Fixture(async f=>
            {
                await f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None);
                var binding=await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,"width"),"Default",CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Active,binding.Status);
                Assert.AreEqual("width",JObject.Parse(binding.SemanticFingerprintJson).Value<string>("role"));
                Assert.AreEqual(CadV2MutationAttemptStatus.Applied,(await f.Repository.GetV2MutationAttemptAsync(f.Attempt.Id,CancellationToken.None)).Status);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"rectangle",f.Model,CancellationToken.None));
            });
        }
        [TestMethod] public async Task GenericOutcomeCannotBypassParametricCaptureValidation()
        {
            await Fixture(async f=>
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2SequentialOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None));
                Assert.AreEqual(CadV2MutationAttemptStatus.Prepared,(await f.Repository.GetV2MutationAttemptAsync(f.Attempt.Id,CancellationToken.None)).Status);
            });
        }
        [TestMethod] public async Task RejectsForeignDuplicateAndMissingCaptures()
        {
            await Fixture(async f=>
            {
                var original=f.Result.Data.DeepClone();
                f.Result.Data["parametricCapture"]["modelId"]=Guid.NewGuid().ToString();
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None));
                f.Result.Data=(JObject)original.DeepClone();((JArray)f.Result.Data["parametricCapture"]["roles"]).Add(f.Result.Data["parametricCapture"]["roles"][0].DeepClone());
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None));
                f.Result.Data=(JObject)original.DeepClone();((JArray)f.Result.Data["parametricCapture"]["dimensions"]).RemoveAt(0);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None));
                Assert.IsNull(await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,"width"),"Default",CancellationToken.None));
            });
        }
        [TestMethod] public async Task FailedOutcomeCommitRollsBackEveryBindingAndCanBeMarkedUncertain()
        {
            await Fixture(async f=>
            {
                using(var c=new System.Data.SQLite.SQLiteConnection("Data Source="+f.Path+";Version=3;"))
                {c.Open();using(var cmd=c.CreateCommand()){cmd.CommandText="CREATE TRIGGER FailParametric BEFORE UPDATE OF Status ON V2MutationAttempts WHEN NEW.StepKey='rectangle' AND NEW.Status='Applied' BEGIN SELECT RAISE(ABORT,'forced'); END;";cmd.ExecuteNonQuery();}}
                await Assert.ThrowsExceptionAsync<System.Data.SQLite.SQLiteException>(()=>f.Repository.CommitV2ParametricOutcomeAsync(f.Job,f.Revision,"rectangle",f.Model,f.Result,CancellationToken.None));
                Assert.IsNull(await f.Repository.GetEntityBindingAsync(f.Model,CadParametricIdentity.For(f.Model,f.Sketch,"width"),"Default",CancellationToken.None));
                await f.Repository.MarkV2MutationAttemptUncertainAsync(f.Attempt.Id,CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"rectangle",f.Model,CancellationToken.None));
            });
        }
        private sealed class Data
        {public SqliteJobRepository Repository;public string Path;public Guid Job,Revision,Model,Sketch;public CadV2MutationAttemptRecord Attempt;public CadCommandResult Result;}
        private static async Task Fixture(Func<Data,Task> action)
        {
            string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"parametric-store-"+Guid.NewGuid()+".db");
            try{using(var repository=new SqliteJobRepository(path))
            {
                await repository.InitializeAsync();var plan=CadParametricPlateContractTests.Plate();var now=DateTime.UtcNow;
                var f=new Data{Repository=repository,Path=path,Job=Guid.NewGuid(),Revision=Guid.NewGuid(),Model=Guid.NewGuid(),Sketch=plan.Steps[1].OutputEntityId.Value};
                await repository.CreateAsync(new CadJob{Id=f.Job,Prompt="parametric",State=JobState.Executing,PlanValidated=true,CreatedUtc=now,UpdatedUtc=now});
                await repository.AppendRevisionAsync(new JobRevision{Id=f.Revision,JobId=f.Job,RevisionNumber=1,Prompt="parametric",PlanJson=JsonConvert.SerializeObject(plan),CreatedUtc=now});
                await repository.PrepareV2NewPartAttemptAsync(f.Job,f.Revision,"new",new CadModelIdentityRecord{ModelId=f.Model,DocumentKind="Part",Status=CadModelIdentityStatus.Pending,CustomPropertyKey="SolidWorksCadAgent.ModelId",CurrentModelRevisionId=Guid.NewGuid(),ConfigurationKey="Default",RegistryVersion=1,CreatedUtc=now,UpdatedUtc=now},CancellationToken.None);
                await repository.CommitV2NewPartOutcomeAsync(f.Job,f.Revision,"new",f.Model,CadCommandResult.Ok(new{modelId=f.Model.ToString("D"),documentTitle="Part1",configurationKey="Default"}),CancellationToken.None);
                var sketch=await repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"sketch",f.Model,CancellationToken.None);
                var binding=await repository.GetEntityBindingAsync(f.Model,f.Sketch,"Default",CancellationToken.None);
                binding.NativeReferenceBytes=new byte[]{1,2,3};binding.Status=CadEntityReferenceStatus.Active;await repository.UpdateEntityBindingAsync(binding,CancellationToken.None);
                await repository.CommitV2SequentialOutcomeAsync(f.Job,f.Revision,"sketch",f.Model,CadCommandResult.Ok(new{entityId=f.Sketch.ToString("D")}),CancellationToken.None);
                f.Attempt=await repository.PrepareV2SequentialAttemptAsync(f.Job,f.Revision,"rectangle",f.Model,CancellationToken.None);
                var observation=new CadParametricPlateObservation{EvidenceVersion=1,ModelId=f.Model,SketchEntityId=f.Sketch,ConfigurationKey="Default",FullyDefined=true,RequiredRelationsPresent=true,ContourEdgeCount=4,ConstructionEdgeCount=1};
                foreach(var role in new[]{"bottom","right","top","left","centreDiagonal","width","length"})
                    observation.Roles.Add(new CadParametricRoleCapture{EntityId=CadParametricIdentity.For(f.Model,f.Sketch,role),Role=role,OwnerKind=role=="width"||role=="length"?"DimensionOwnerFeature":"SketchSegment",OwnerReferenceBytes=new byte[]{4,5,6},NativeDimensionFullName=role=="width"||role=="length"?"Agent"+role+"@Sketch1@Part1":null});
                foreach(var role in new[]{"width","length"})observation.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(f.Model,f.Sketch,role),Role=role,OwnerEntityId=f.Sketch,ValueMm=role=="width"?120:60,IsDriving=true});
                f.Result=CadCommandResult.Ok(new{centerXmm=0,centerYmm=0,widthMm=120,heightMm=60,parametricCapture=observation});await action(f);
            }}finally{foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        }
    }
}
