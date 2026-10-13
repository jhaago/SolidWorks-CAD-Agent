using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ParametricEditCoordinatorTests
    {
        [TestMethod] public async Task MissingOrUnverifiedSourceFailsWithoutCallingProvider()
        {
            var root=Path.Combine(Path.GetTempPath(),"edit-coordinator-"+Guid.NewGuid().ToString("N"));
            using(var repository=new SqliteJobRepository(Path.Combine(root,"jobs.sqlite"))) {
                await repository.InitializeAsync();
                var coordinator=new JobCoordinator(repository,new NoPlanner(),new SimulatedCadCommandExecutor(),new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Real,AutoMode=true});
                var error=await Assert.ThrowsExceptionAsync<JobCoordinatorException>(()=>coordinator.CreateAndPlanAsync("V2 parametric edit: job "+Guid.NewGuid()+"; parameter width = 150 mm; save as result.sldprt",CancellationToken.None));
                Assert.AreEqual("PARAMETRIC_EDIT_SOURCE_UNAVAILABLE",error.Code);
                Assert.IsFalse(coordinator.SupportsCheckpointedParametricEditing);
            }
        }
        [TestMethod] public async Task MalformedExplicitEditIsRejectedBeforeJobCreation()
        {
            var root=Path.Combine(Path.GetTempPath(),"edit-coordinator-"+Guid.NewGuid().ToString("N"));
            using(var repository=new SqliteJobRepository(Path.Combine(root,"jobs.sqlite"))) {
                await repository.InitializeAsync();
                var coordinator=new JobCoordinator(repository,new NoPlanner(),new SimulatedCadCommandExecutor(),new AgentSettings{WorkspaceRoot=root,ExecutionMode=ExecutionMode.Simulation});
                var error=await Assert.ThrowsExceptionAsync<JobCoordinatorException>(()=>coordinator.CreateAndPlanAsync("V2 parametric edit: arbitrary file; parameter width=NaN mm",CancellationToken.None));
                Assert.AreEqual("PARAMETRIC_REQUEST_INVALID",error.Code);
            }
        }
        private sealed class NoPlanner:ICadPlanningProvider {
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit edit syntax cannot fall through to a provider.");
        }
    }
}
