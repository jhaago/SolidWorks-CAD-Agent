using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadPlanV2ExecutionReadinessTests
    {
        private const string ExtrudeCandidate = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"rectangle\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}]}";

        [TestMethod]
        public void RealExtrudePlanPreflightBindsAllStepsWithoutDispatch()
        {
            var normalized = Normalize(ExtrudeCandidate);
            var executionId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            var result = CadPlanV2ExecutionReadiness.Assess(normalized, ExecutionMode.Real, new RegisteredExecutor(), executionId, modelId);
            Assert.IsTrue(result.IsReady, string.Join(" ", result.Errors));
            Assert.AreEqual(5, result.Requests.Count);
            Assert.IsTrue(result.Requests.All(request => request.Command.ExecutionId == executionId &&
                request.Command.ManagedModelId == modelId));
            Assert.AreEqual(2, result.Requests.Last().OperationVersion);
            Assert.AreEqual(result.Requests[1].Command.OutputEntityId, result.Requests[2].TargetSketchEntityId);
            Assert.AreEqual(result.Requests[1].Command.OutputEntityId, result.Requests[3].TargetSketchEntityId);
            Assert.AreEqual(result.Requests[1].Command.OutputEntityId, result.Requests.Last().ProfileSketchEntityId);
            Assert.AreEqual(5.0, result.Requests.Last().Command.Parameters.Value<double>("depthMm"));
        }

        [TestMethod]
        public void UnsupportedCutRejectsWholePlanBeforeDispatch()
        {
            var cut = ExtrudeCandidate.Replace("\"stepKey\":\"boss\",\"command\":\"Extrude\"",
                "\"stepKey\":\"cut\",\"command\":\"CutExtrude\"")
                .Replace("\"depthMm\":5", "\"endCondition\":\"ThroughAll\"");
            var normalized = Normalize(cut);
            var result = CadPlanV2ExecutionReadiness.Assess(normalized, ExecutionMode.Real, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(result.IsReady);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("cut") && error.Contains("CutExtrude")),
                string.Join(" ", result.Errors));
            Assert.AreEqual(0, result.Requests.Count, "An incomplete whole plan must not expose executable requests.");
        }

        [TestMethod]
        public void SimulationAndUnnormalizedPlansNeverBecomeReady()
        {
            var simulated = CadPlanV2ExecutionReadiness.Assess(Normalize(ExtrudeCandidate),
                ExecutionMode.Simulation, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(simulated.IsReady);
            Assert.IsTrue(simulated.Errors.Any(error => error.Contains("simulation")), string.Join(" ", simulated.Errors));
            Assert.AreEqual(0, simulated.Requests.Count);

            var unnormalized = CadPlanV2ExecutionReadiness.Assess(ExtrudeCandidate,
                ExecutionMode.Real, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(unnormalized.IsReady);
            Assert.AreEqual(0, unnormalized.Requests.Count);
        }

        [TestMethod]
        public void RegistrationWithoutVersionedDispatchCannotClaimReady()
        {
            var result = CadPlanV2ExecutionReadiness.Assess(Normalize(ExtrudeCandidate),
                ExecutionMode.Real, new RegistrationOnlyExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(result.IsReady);
            Assert.AreEqual(0, result.Requests.Count);
        }

        [TestMethod]
        public void SaveRequestCannotCarryPlannerOverwritePermissionIntoExecutionPreview()
        {
            var candidate = JObject.Parse(ExtrudeCandidate);
            ((JArray)candidate["steps"]).Add(JObject.FromObject(new
            {
                stepKey = "save",
                command = CadCommandNames.SavePart,
                operationVersion = 1,
                parameters = new { path = "review-only-part.sldprt", allowOverwrite = true }
            }));
            var result = CadPlanV2ExecutionReadiness.Assess(Normalize(candidate.ToString()),
                ExecutionMode.Real, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(result.IsReady);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("SavePart")), string.Join(" ", result.Errors));
            Assert.AreEqual(0, result.Requests.Count);
        }

        [TestMethod]
        public void NarrowFinalSaveCanBePreviewedWithoutGrantingExecution()
        {
            var candidate = JObject.Parse(ExtrudeCandidate);
            ((JArray)candidate["steps"]).Add(JObject.FromObject(new
            {
                stepKey = "save", command = CadCommandNames.SavePart, operationVersion = 1,
                parameters = new { path = "review-only-part.sldprt", allowOverwrite = false }
            }));
            var executionId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            var result = CadPlanV2ExecutionReadiness.Assess(Normalize(candidate.ToString()),
                ExecutionMode.Real, new RegisteredExecutor(), executionId, modelId);
            Assert.IsTrue(result.IsReady, string.Join(" ", result.Errors));
            Assert.AreEqual(6, result.Requests.Count);
            var save = result.Requests.Last();
            Assert.AreEqual(CadCommandNames.SavePart, save.Command.Command);
            Assert.AreEqual(executionId, save.Command.ExecutionId);
            Assert.AreEqual(modelId, save.Command.ManagedModelId);
            Assert.AreEqual("review-only-part.sldprt", save.Command.Parameters.Value<string>("path"));
            Assert.AreEqual(false, save.Command.Parameters.Value<bool>("allowOverwrite"));
            var unavailable = CadPlanV2ExecutionReadiness.Assess(Normalize(candidate.ToString()),
                ExecutionMode.Real, new RegisteredExecutor { SupportsSave = false }, executionId, modelId);
            Assert.IsFalse(unavailable.IsReady);
            Assert.AreEqual(0, unavailable.Requests.Count);
            Assert.IsTrue(unavailable.Errors.Any(error => error.Contains("SavePart")), string.Join(" ", unavailable.Errors));
        }

        [TestMethod]
        public void FinalSaveScopeRejectsOffsetRectangleAndOtherSketchPlane()
        {
            var candidate = JObject.Parse(ExtrudeCandidate);
            ((JArray)candidate["steps"]).Add(JObject.FromObject(new
            {
                stepKey = "save", command = CadCommandNames.SavePart, operationVersion = 1,
                parameters = new { path = "part.sldprt", allowOverwrite = false }
            }));
            var steps = (JArray)candidate["steps"];
            steps[2]["parameters"]["centerXmm"] = 3;
            var offset = CadPlanV2ExecutionReadiness.Assess(Normalize(candidate.ToString()),
                ExecutionMode.Real, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(offset.IsReady);
            Assert.AreEqual(0, offset.Requests.Count);
            steps[2]["parameters"]["centerXmm"] = 0;
            steps[1]["parameters"]["plane"] = "Front Plane";
            var plane = CadPlanV2ExecutionReadiness.Assess(Normalize(candidate.ToString()),
                ExecutionMode.Real, new RegisteredExecutor(), Guid.NewGuid(), Guid.NewGuid());
            Assert.IsFalse(plane.IsReady);
            Assert.AreEqual(0, plane.Requests.Count);
        }

        [TestMethod]
        public void BridgeWithoutManagedReferenceStoreCannotClaimReady()
        {
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var result = CadPlanV2ExecutionReadiness.Assess(Normalize(ExtrudeCandidate),
                    ExecutionMode.Real, bridge, Guid.NewGuid(), Guid.NewGuid());
                Assert.IsFalse(result.IsReady);
                Assert.AreEqual(0, result.Requests.Count);
            }
        }

        private class RegistrationOnlyExecutor : ICadCommandExecutor, ICadOperationAvailability
        {
            public bool SupportsManagedReferences => true;
            public bool SupportsSave { get; set; } = true;
            public bool IsOperationRegistered(string commandName, int operationVersion) =>
                operationVersion == 1 && (commandName == CadCommandNames.NewPart ||
                    commandName == CadCommandNames.CreateSketch || commandName == CadCommandNames.AddRectangle ||
                    commandName == CadCommandNames.ExitSketch || commandName == CadCommandNames.SavePart && SupportsSave) ||
                operationVersion == 2 && commandName == CadCommandNames.Extrude;

            public Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken cancellationToken) =>
                throw new AssertFailedException("Readiness must never execute a command.");
        }

        private sealed class RegisteredExecutor : RegistrationOnlyExecutor, IVersionedCadCommandExecutor
        {
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken cancellationToken) =>
                throw new AssertFailedException("Readiness must never execute a command.");
        }

        private static string Normalize(string candidate)
        {
            var parsed = CadPlanDocumentReader.ReadCandidate(candidate);
            Assert.IsTrue(parsed.IsValid, string.Join(" ", parsed.Errors));
            return JsonConvert.SerializeObject(CadPlanV2HostNormalizer.Normalize(parsed.CandidateV2));
        }
    }
}
