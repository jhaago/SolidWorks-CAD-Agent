using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ParametricEditPersistenceTests
    {
        [TestMethod] public async Task ForeignCaptureFingerprintCannotCommitAnOtherwiseValidCheckpoint()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();var checkpoint=await f.Checkpoint(attempt);
                var binding=checkpoint.Bindings.First(b=>b.EntityKind=="DrivingDimension");
                var fingerprint=JObject.Parse(binding.SemanticFingerprintJson);fingerprint["modelId"]=Guid.NewGuid().ToString("D");binding.SemanticFingerprintJson=fingerprint.ToString();
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.CommitParametricEditCheckpointAsync(attempt.Id,checkpoint,CancellationToken.None));
                Assert.IsNull(await f.Repository.GetEntityBindingAsync(attempt.ModelId,binding.EntityId,"Default",CancellationToken.None));
            });
        }
        [TestMethod] public async Task FailedCheckpointCommitRollsBackBindingsAndJournalTogether()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();var checkpoint=await f.Checkpoint(attempt);
                using(var c=new SQLiteConnection("Data Source="+f.Database+";Version=3;")){c.Open();using(var cmd=c.CreateCommand()){cmd.CommandText="CREATE TRIGGER FailEdit BEFORE UPDATE OF Status ON ParametricEditAttempts WHEN NEW.Status='Checkpointed' BEGIN SELECT RAISE(ABORT,'forced'); END;";cmd.ExecuteNonQuery();}}
                await Assert.ThrowsExceptionAsync<SQLiteException>(()=>f.Repository.CommitParametricEditCheckpointAsync(attempt.Id,checkpoint,CancellationToken.None));
                Assert.AreEqual(CadParametricEditAttemptStatus.Prepared,(await f.Repository.GetParametricEditAttemptAsync(f.Job,f.Revision,CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.Pending,(await f.Repository.GetModelAsync(attempt.ModelId,CancellationToken.None)).Status);
                foreach(var binding in checkpoint.Bindings)Assert.IsNull(await f.Repository.GetEntityBindingAsync(attempt.ModelId,binding.EntityId,"Default",CancellationToken.None));
            });
        }
        [TestMethod] public async Task ReservationCreatesChildOwnershipAndRejectsReplay()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();
                Assert.AreEqual(CadParametricEditAttemptStatus.Prepared,attempt.Status);
                var child=await f.Repository.GetModelAsync(attempt.ModelId,CancellationToken.None);
                Assert.AreEqual(f.Source.ModelId,child.ParentModelId);
                Assert.AreEqual(CadModelIdentityStatus.Pending,child.Status);
                Assert.AreEqual(f.Job,await f.Repository.GetV2ModelOwnerAsync(child.ModelId,CancellationToken.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Reserve());
                Assert.AreEqual(f.Source.ModelRevisionId,(await f.Repository.GetModelAsync(f.Source.ModelId,CancellationToken.None)).CurrentModelRevisionId);
            });
        }
        [TestMethod] public async Task StaleSourceAndWrongJobStateCannotReserve()
        {
            await Fixture(async f=>{
                var source=await f.Repository.GetModelAsync(f.Source.ModelId,CancellationToken.None);source.CurrentModelRevisionId=Guid.NewGuid();
                await f.Repository.UpdateModelAsync(source,CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Reserve());
                source.CurrentModelRevisionId=f.Source.ModelRevisionId;await f.Repository.UpdateModelAsync(source,CancellationToken.None);
                var job=await f.Repository.GetAsync(f.Job);job.State=JobState.Cancelled;await f.Repository.UpdateAsync(job);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Reserve());
            });
        }
        [TestMethod] public async Task StartupMakesInterruptedEditsUncertainWithoutChangingSource()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();
                Assert.AreEqual(1,await f.Repository.RecoverInterruptedJobsAsync());
                var recovered=await f.Repository.GetParametricEditAttemptAsync(f.Job,f.Revision,CancellationToken.None);
                Assert.AreEqual(CadParametricEditAttemptStatus.Uncertain,recovered.Status);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain,(await f.Repository.GetModelAsync(attempt.ModelId,CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.ActiveSaved,(await f.Repository.GetModelAsync(f.Source.ModelId,CancellationToken.None)).Status);
                Assert.AreEqual(JobState.Failed,(await f.Repository.GetAsync(f.Job)).State);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Reserve());
            });
        }
        [TestMethod] public async Task StartedMutationRecoveryMarksAllChildReferencesUncertainAndNeverReplays()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();var checkpoint=await f.Checkpoint(attempt);
                await f.Repository.CommitParametricEditCheckpointAsync(attempt.Id,checkpoint,CancellationToken.None);
                await f.Repository.BeginParametricEditMutationAsync(attempt.Id,CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.BeginParametricEditMutationAsync(attempt.Id,CancellationToken.None));
                await f.Repository.RecoverInterruptedJobsAsync();
                Assert.AreEqual(CadParametricEditAttemptStatus.Uncertain,(await f.Repository.GetParametricEditAttemptAsync(f.Job,f.Revision,CancellationToken.None)).Status);
                foreach(var binding in checkpoint.Bindings)Assert.AreEqual(CadEntityReferenceStatus.Uncertain,(await f.Repository.GetEntityBindingAsync(attempt.ModelId,binding.EntityId,"Default",CancellationToken.None)).Status);
                Assert.AreEqual(CadModelIdentityStatus.ActiveSaved,(await f.Repository.GetModelAsync(f.Source.ModelId,CancellationToken.None)).Status);
            });
        }
        [TestMethod] public async Task PreparedAttemptCannotSkipCheckpointOrPublish()
        {
            await Fixture(async f=>{
                var attempt=await f.Reserve();
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.BeginParametricEditMutationAsync(attempt.Id,CancellationToken.None));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.PublishParametricEditAsync(attempt.Id,new Newtonsoft.Json.Linq.JObject(),CancellationToken.None));
                Assert.AreEqual(CadParametricEditAttemptStatus.Prepared,(await f.Repository.GetParametricEditAttemptAsync(f.Job,f.Revision,CancellationToken.None)).Status);
            });
        }
        [TestMethod] public async Task SchemaSevenUpgradesToEightAndFutureVersionRollsBack()
        {
            await Fixture(async f=>{
                using(var c=new SQLiteConnection("Data Source="+f.Database+";Version=3;")){c.Open();using(var cmd=c.CreateCommand()){cmd.CommandText="DROP TABLE ParametricEditAttempts; PRAGMA user_version=7;";cmd.ExecuteNonQuery();}}
                await f.Repository.InitializeAsync();Assert.AreEqual(8L,Scalar(f.Database,"PRAGMA user_version;"));
                Assert.AreEqual(1L,Scalar(f.Database,"SELECT COUNT(*) FROM ManagedModels;"));
                using(var c=new SQLiteConnection("Data Source="+f.Database+";Version=3;")){c.Open();using(var cmd=c.CreateCommand()){cmd.CommandText="DROP TABLE ParametricEditAttempts; PRAGMA user_version=9;";cmd.ExecuteNonQuery();}}
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>f.Repository.InitializeAsync());
                Assert.AreEqual(9L,Scalar(f.Database,"PRAGMA user_version;"));
                Assert.AreEqual(0L,Scalar(f.Database,"SELECT COUNT(*) FROM sqlite_master WHERE name='ParametricEditAttempts';"));
            });
        }
        private static long Scalar(string path,string sql)
        {
            using(var c=new SQLiteConnection("Data Source="+path+";Version=3;")){c.Open();using(var cmd=c.CreateCommand()){cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}}
        }
        private sealed class Data {
            public SqliteJobRepository Repository; public string Root,Database;public Guid Job,Revision;public CadParametricEditSource Source;
            public Task<CadParametricEditAttemptRecord> Reserve()=>Repository.PrepareParametricEditAttemptAsync(Job,Revision,Guid.NewGuid(),new WorkspacePolicy(Root),CancellationToken.None);
            public async Task<CadParametricEditCheckpoint> Checkpoint(CadParametricEditAttemptRecord attempt)
            {
                var baseline=CadPlanDocumentReader.ReadPersisted(Source.SourcePlanJson).CandidateV2;var sketch=baseline.Steps[1].OutputEntityId.Value;
                var model=await Repository.GetModelAsync(attempt.ModelId,CancellationToken.None);var now=DateTime.UtcNow;
                var bindings=new System.Collections.Generic.List<CadEntityReferenceBinding>{new CadEntityReferenceBinding{ModelId=model.ModelId,EntityId=sketch,EntityKind="Sketch",NativeObjectKind="SketchFeature",ConfigurationKey=model.ConfigurationKey,
                    ReferenceFormatVersion=1,NativeReferenceBytes=new byte[]{1,2},CreatedAtModelRevisionId=attempt.CheckpointModelRevisionId,Status=CadEntityReferenceStatus.Active,CreatedUtc=now,UpdatedUtc=now}};
                foreach(var role in CadParametricIdentity.Roles) {
                    var dimension=role=="width"||role=="length"||role=="thickness";var owner=role=="boss"||role=="thickness"?CadParametricIdentity.For(model.ModelId,sketch,"boss"):sketch;
                    bindings.Add(new CadEntityReferenceBinding{ModelId=model.ModelId,EntityId=CadParametricIdentity.For(model.ModelId,sketch,role),EntityKind=dimension?"DrivingDimension":role=="boss"?"Feature":"SketchSegment",NativeObjectKind=dimension?"DimensionOwnerFeature":role=="boss"?"Feature":"SketchSegment",ConfigurationKey=model.ConfigurationKey,
                        ReferenceFormatVersion=3,NativeReferenceBytes=new byte[]{3,4},CreatedAtModelRevisionId=attempt.CheckpointModelRevisionId,Status=CadEntityReferenceStatus.Active,CreatedUtc=now,UpdatedUtc=now,
                        SemanticFingerprintJson=new JObject{{"evidenceVersion",1},{"modelId",model.ModelId.ToString("D")},{"sketchEntityId",sketch.ToString("D")},{"role",role},{"configurationKey",model.ConfigurationKey},{"ownerEntityId",owner.ToString("D")},
                            {"nativeDimensionName",dimension?role=="width"?"AgentWidth":role=="length"?"AgentLength":"AgentThickness":null}}.ToString()});
                }
                var intent=CadParametricPlateContract.ReadIntent(baseline);
                var plate=new CadParametricPlateObservation{EvidenceVersion=1,ModelId=model.ModelId,SketchEntityId=sketch,ConfigurationKey=model.ConfigurationKey,FullyDefined=true,RequiredRelationsPresent=true,ContourEdgeCount=4,ConstructionEdgeCount=1,SolidBodyCount=1};
                foreach(var role in new[]{"width","length","thickness"})plate.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(model.ModelId,sketch,role),Role=role,OwnerEntityId=role=="thickness"?CadParametricIdentity.For(model.ModelId,sketch,"boss"):sketch,IsDriving=true,ValueMm=role=="width"?intent.WidthMm:role=="length"?intent.LengthMm:intent.ThicknessMm});
                var geometry=CadV2PrismaticGeometry.Analyze(baseline);
                return new CadParametricEditCheckpoint{ModelId=model.ModelId,WorkingPath=attempt.WorkingPath,CheckpointPath=attempt.CheckpointPath,Sha256=new string('b',64),Bindings=bindings,
                    Observed=CadCommandResult.Ok(new{parametricPlate=plate,parametricHole=(object)null,bodies=new{bodyCount=1,volumeMm3=geometry.VolumeMm3},bounds=geometry.ExpectedBounds(),nativeCuts=new JArray(),hasRebuildErrors=false}).Data};
            }
        }
        private static async Task Fixture(Func<Data,Task> test)
        {
            var root=Path.Combine(Path.GetTempPath(),"edit-journal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var f=new Data{Root=root,Database=Path.Combine(root,"jobs.sqlite"),Job=Guid.NewGuid(),Revision=Guid.NewGuid(),Source=CadParametricEditContractTests.Source(false)};
            using(f.Repository=new SqliteJobRepository(f.Database)) {
                await f.Repository.InitializeAsync();var now=DateTime.UtcNow;
                await f.Repository.RegisterModelAsync(new CadModelIdentityRecord{ModelId=f.Source.ModelId,CurrentModelRevisionId=f.Source.ModelRevisionId,
                    ConfigurationKey=f.Source.ConfigurationKey,CanonicalPath=Path.Combine(root,f.Source.Path.Replace('/',Path.DirectorySeparatorChar)),LastSavedSha256=f.Source.Sha256,
                    Status=CadModelIdentityStatus.ActiveSaved,DocumentKind="Part",CustomPropertyKey="SolidWorksCadAgent.ModelId",RegistryVersion=1,CreatedUtc=now,UpdatedUtc=now},CancellationToken.None);
                await f.Repository.CreateAsync(new CadJob{Id=f.Job,Prompt="edit",State=JobState.Executing,PlanValidated=true,RequiresExplicitApproval=true,CreatedUtc=now,UpdatedUtc=now});
                await f.Repository.AppendRevisionAsync(new JobRevision{Id=f.Revision,JobId=f.Job,RevisionNumber=1,Prompt="edit",PlanJson=JsonConvert.SerializeObject(CadParametricEditContract.Build(f.Source,"width",150,"result.sldprt")),CreatedUtc=now});
                await test(f);
            }
        }
    }
}
