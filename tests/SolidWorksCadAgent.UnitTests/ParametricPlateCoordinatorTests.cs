using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class ParametricPlateCoordinatorTests
    {
        [TestMethod] public void ExplicitIntakeUsesTheNewPairAndNamedParameters()
        {
            var parsed=CadPlanDocumentReader.ReadCandidate(CadParametricPlatePrompt.BuildCandidate("V2 parametric plate: 120 x 60 x 10 mm; save as parametric/plate.sldprt"));
            Assert.IsTrue(parsed.IsValid,string.Join(" ",parsed.Errors));
            var plan=CadPlanV2HostNormalizer.Normalize(parsed.CandidateV2);var intent=CadParametricPlateContract.ReadIntent(plan);
            Assert.AreEqual(120.0,intent.WidthMm);Assert.AreEqual(60.0,intent.LengthMm);Assert.AreEqual(10.0,intent.ThicknessMm);
            Assert.AreEqual(2,plan.Steps[2].OperationVersion);Assert.AreEqual(3,plan.Steps[4].OperationVersion);
        }
        [TestMethod] public void MalformedAndUnsupportedInputsDoNotBecomeLegacyPlans()
        {
            foreach(var prompt in new[]{"V2 parametric plate: 120 x 60 mm; save as plate.sldprt","V2 parametric plate: 120 x 60 x 10 mm; save as C:/plate.sldprt","V2 parametric plate: NaN x 60 x 10 mm; save as plate.sldprt"})
                Assert.ThrowsException<ArgumentException>(()=>CadParametricPlatePrompt.BuildCandidate(prompt));
        }
        [TestMethod] public async Task SimulationNeverAdvertisesNativeParametersAndExplicitIntakeAvoidsCloudPlanning()
        {
            var path=Path.Combine(Path.GetTempPath(),"parametric-intake-"+Guid.NewGuid()+".db");
            try{using(var repository=new SqliteJobRepository(path))
            {
                await repository.InitializeAsync();
                var coordinator=new JobCoordinator(repository,new NeverPlanner(),new SimulatedCadCommandExecutor(),new AgentSettings{WorkspaceRoot=Path.GetTempPath(),ExecutionMode=ExecutionMode.Simulation});
                Assert.IsFalse(coordinator.SupportsParametricPlateDimensions);
                var result=await coordinator.CreateAndPlanAsync("V2 parametric plate: 120 x 60 x 10 mm; save as plate.sldprt",CancellationToken.None);
                Assert.AreEqual(JobState.AwaitingClarification,result.Job.State);Assert.AreEqual(0,result.Commands.Count);
            }}finally{foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
        }
        private sealed class NeverPlanner:ICadPlanningProvider
        {public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request,CancellationToken token)=>throw new AssertFailedException("Explicit parametric syntax must not call a provider.");}
    }
}
