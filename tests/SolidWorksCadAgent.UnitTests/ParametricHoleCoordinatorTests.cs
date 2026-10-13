using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class ParametricHoleCoordinatorTests
    {
        private const string Prompt="V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as hole.sldprt";
        [TestMethod] public async Task ExplicitNativeIntakeRequiresCurrentApprovalEvenInAutoModeAndNeverCallsPlanner()
        {
            await Fixture(async repository=>{
                var executor=new RegisteredExecutor();var coordinator=new JobCoordinator(repository,new NeverPlanner(),executor,new AgentSettings{WorkspaceRoot=Path.GetTempPath(),ExecutionMode=ExecutionMode.Real,AutoMode=true});
                Assert.IsTrue(coordinator.SupportsParametricHoleDimensions);
                var snapshot=await coordinator.CreateAndPlanAsync(Prompt,CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingApproval,snapshot.Job.State);Assert.AreEqual(0,snapshot.Commands.Count);
                Assert.AreEqual(10,CadPlanDocumentReader.ReadPersisted(snapshot.Revisions[0].PlanJson).CandidateV2.Steps.Count);
                var error=await Assert.ThrowsExceptionAsync<JobCoordinatorException>(()=>coordinator.ApproveAndExecuteAsync(snapshot.Job.Id,Guid.NewGuid(),CancellationToken.None));
                Assert.AreEqual("STALE_PLAN",error.Code);
            });
        }
        [TestMethod] public async Task SimulationOrMissingCircleRegistrationRejectBeforeAnyExecution()
        {
            await Fixture(async repository=>{
                foreach(var simulation in new[]{true,false}){
                    ICadCommandExecutor executor=simulation?(ICadCommandExecutor)new SimulatedCadCommandExecutor():new RegisteredExecutor{Circle=false};
                    var coordinator=new JobCoordinator(repository,new NeverPlanner(),executor,new AgentSettings{WorkspaceRoot=Path.GetTempPath(),ExecutionMode=simulation?ExecutionMode.Simulation:ExecutionMode.Real});
                    Assert.IsFalse(coordinator.SupportsParametricHoleDimensions);
                    var snapshot=await coordinator.CreateAndPlanAsync(Prompt,CancellationToken.None);
                    Assert.AreEqual(JobState.AwaitingClarification,snapshot.Job.State);Assert.AreEqual(0,snapshot.Commands.Count);
                }
            });
        }
        [TestMethod] public async Task InvalidExplicitSyntaxCannotFallThroughToProvider()
        {
            await Fixture(async repository=>{
                var coordinator=new JobCoordinator(repository,new NeverPlanner(),new RegisteredExecutor(),new AgentSettings{WorkspaceRoot=Path.GetTempPath(),ExecutionMode=ExecutionMode.Real});
                var error=await Assert.ThrowsExceptionAsync<JobCoordinatorException>(()=>coordinator.CreateAndPlanAsync(Prompt.Replace("x=20","x=0"),CancellationToken.None));
                Assert.AreEqual("PARAMETRIC_REQUEST_INVALID",error.Code);
            });
        }
        private sealed class NeverPlanner:ICadPlanningProvider
        {public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit hole syntax must avoid provider calls.");}
        private sealed class RegisteredExecutor:ICadCommandExecutor,ICadOperationAvailability,IVersionedCadCommandExecutor
        {
            public bool Circle=true;public bool SupportsManagedReferences=>true;
            public bool IsOperationRegistered(string name,int version)=>version==1||name==CadCommandNames.Extrude&&version==3||name==CadCommandNames.AddRectangle&&version==2||name==CadCommandNames.CutExtrude&&version==2||name==CadCommandNames.AddCircle&&version==2&&Circle;
            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command,CancellationToken token)=>throw new AssertFailedException("No unapproved native execution.");
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request,CancellationToken token)=>throw new AssertFailedException("No unapproved native execution.");
        }
        private static async Task Fixture(Func<SqliteJobRepository,Task> action)
        {
            var path=Path.Combine(Path.GetTempPath(),"hole-intake-"+Guid.NewGuid()+".db");
            try{using(var repository=new SqliteJobRepository(path)){await repository.InitializeAsync();await action(repository);}}
            finally{foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        }
    }
}
