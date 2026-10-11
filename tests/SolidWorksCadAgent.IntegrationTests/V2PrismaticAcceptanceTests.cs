using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("SolidWorksIntegration")]
    [TestCategory("PersistentReference")]
    public class V2PrismaticAcceptanceTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod, Timeout(240000)]
        public Task OffsetCircularBoss_ApprovedJobVerifiesTopPlaneAxesAfterReopen()
        {
            var plan = NewPlan();
            Group(plan, "AddCircle", new { centerXmm = 7.0, centerYmm = 11.0, diameterMm = 12.0 }, new { depthMm = 5.0 });
            return RunAsync("offset-circle", plan, 180 * Math.PI,
                new[] { 1.0, 13.0, 0.0, 5.0, -17.0, -5.0 }, 1);
        }

        [TestMethod, Timeout(240000)]
        public Task RotatedSlotBoss_ApprovedJobVerifiesCapsuleAreaAndBoundsAfterReopen()
        {
            var plan = NewPlan();
            Group(plan, "AddSlot", new { centerXmm = 0.0, centerYmm = 0.0, lengthMm = 30.0, widthMm = 10.0, angleDegrees = 30.0 }, new { depthMm = 5.0 });
            double halfX = 5 + 5 * Math.Sqrt(3);
            return RunAsync("rotated-slot", plan, (200 + 25 * Math.PI) * 5,
                new[] { -halfX, halfX, 0.0, 5.0, -10.0, 10.0 }, 1);
        }

        [TestMethod, Timeout(240000)]
        public Task RotatedHexagonBoss_ApprovedJobVerifiesVertexBoundsAfterReopen()
        {
            var plan = NewPlan();
            Group(plan, "AddRegularPolygon", new { centerXmm = 0.0, centerYmm = 0.0, sides = 6, diameterMm = 16.0, angleDegrees = 30.0 }, new { depthMm = 5.0 });
            double halfX = 4 * Math.Sqrt(3);
            return RunAsync("rotated-hexagon", plan, 480 * Math.Sqrt(3),
                new[] { -halfX, halfX, 0.0, 5.0, -8.0, 8.0 }, 1);
        }

        [TestMethod, Timeout(300000)]
        public Task RectangularPlateWithFourDisjointCuts_ApprovedJobVerifiesThroughAndBlindDepthsAfterReopen()
        {
            var plan = NewPlan();
            Group(plan, "AddRectangle", new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 120.0, heightMm = 60.0 }, new { depthMm = 10.0 });
            Group(plan, "AddCircle", new { centerXmm = -40.0, centerYmm = 0.0, diameterMm = 10.0 }, new { endCondition = "ThroughAll" });
            Group(plan, "AddSlot", new { centerXmm = 0.0, centerYmm = 0.0, lengthMm = 24.0, widthMm = 8.0, angleDegrees = 30.0 }, new { endCondition = "Blind", depthMm = 3.0 });
            Group(plan, "AddRectangle", new { centerXmm = 40.0, centerYmm = 0.0, widthMm = 12.0, heightMm = 16.0 }, new { endCondition = "Blind", depthMm = 4.0 });
            Group(plan, "AddRegularPolygon", new { centerXmm = 0.0, centerYmm = 20.0, sides = 6, diameterMm = 8.0, angleDegrees = 30.0 }, new { endCondition = "Blind", depthMm = 2.0 });
            return RunAsync("four-cuts", plan, 72000 - 250 * Math.PI - (128 + 16 * Math.PI) * 3 - 768 - 48 * Math.Sqrt(3),
                new[] { -60.0, 60.0, 0.0, 10.0, -30.0, 30.0 }, 5);
        }

        private async Task RunAsync(string name, JObject candidate, double expectedVolume, double[] bounds, int groups)
        {
            if (System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS") != "1")
                Assert.Inconclusive("Set SOLIDWORKS_RUN_REFERENCE_TESTS=1 and SOLIDWORKS_EXPECTED_YEAR=2020 for native acceptance.");
            Assert.AreEqual("2020", System.Environment.GetEnvironmentVariable("SOLIDWORKS_EXPECTED_YEAR"));
#if SOLIDWORKS_INTEROP
            string runId = Guid.NewGuid().ToString("N");
            string workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", "v2-" + name + "-" + runId);
            string filename = name + "-" + runId + ".sldprt";
            ((JArray)candidate["steps"]).Add(Step("save", "SavePart", new { path = filename, allowOverwrite = false }));
            Directory.CreateDirectory(workspace);
            Guid jobId = Guid.Empty;
            using (var repository = new SqliteJobRepository(Path.Combine(workspace, "jobs.sqlite")))
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace), repository))
            {
                await repository.InitializeAsync();
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var original = await session.InvokeWithApplicationAsync(app =>
                {
                    var active = ((SldWorks)app).ActiveDoc as ModelDoc2;
                    return active == null ? null : new Original { Title = active.GetTitle(), Path = active.GetPathName(), Dirty = active.GetSaveFlag() };
                }, CancellationToken.None);
                try
                {
                    var coordinator = new JobCoordinator(repository, new FixedPlanner(candidate.ToString()), bridge,
                        new AgentSettings { WorkspaceRoot = workspace, ExecutionMode = ExecutionMode.Real, AutoMode = true });
                    var planned = await coordinator.CreateAndPlanAsync("Native v2 prismatic acceptance " + name, CancellationToken.None);
                    jobId = planned.Job.Id;
                    Assert.AreEqual(JobState.AwaitingApproval, planned.Job.State);
                    Assert.AreEqual(0, planned.Commands.Count, "Planning must not mutate SOLIDWORKS.");
                    var result = await coordinator.ApproveAndExecuteAsync(jobId, planned.Revisions.Single().Id, CancellationToken.None);
                    var detail = string.Join(System.Environment.NewLine, result.Commands.Select(c => c.CommandName + ": " + c.ResultJson));
                    TestContext?.WriteLine("Workspace: " + workspace + System.Environment.NewLine + detail);
                    Assert.AreEqual(JobState.ReadyForReview, result.Job.State, detail);
                    Assert.AreEqual(2 + groups * 4, result.Commands.Count);
                    Assert.IsTrue(result.Commands.All(c => c.Success), detail);
                    CollectionAssert.AreEqual(((JArray)candidate["steps"]).Select(s => (string)s["command"]).ToArray(),
                        result.Commands.Select(c => c.CommandName).ToArray());
                    var evidence = result.Verifications.Last(v => v.CheckName == CadV2SavedVerificationRecorder.CheckName);
                    Assert.IsTrue(evidence.Passed, evidence.ActualJson);
                    var observed = JObject.Parse(evidence.ActualJson)["observed"];
                    Assert.AreEqual(1, observed["bodies"].Value<int>("bodyCount"));
                    Assert.AreEqual(expectedVolume, observed["bodies"].Value<double>("volumeMm3"), Math.Max(0.05, expectedVolume * 0.0001));
                    Assert.IsFalse(observed["rebuild"].Value<bool>("hasErrors"));
                    var keys = new[] { "minXmm", "maxXmm", "minYmm", "maxYmm", "minZmm", "maxZmm" };
                    for (int i = 0; i < keys.Length; i++)
                        Assert.AreEqual(bounds[i], observed["bounds"].Value<double>(keys[i]), 0.02, keys[i]);
                    Assert.AreEqual(bounds[3] - bounds[2], observed["bounds"].Value<double>("sizeYmm"), 0.02, "Extrusion depth along model Y.");
                    string savedPath = Path.Combine(workspace, filename);
                    Assert.AreEqual(savedPath, result.Job.OutputPath, true);
                    Assert.IsTrue(new FileInfo(savedPath).Length > 0);
                    Guid modelId = Guid.Parse(JObject.Parse(result.Commands.First().ResultJson).Value<string>("modelId"));
                    var model = await repository.GetModelAsync(modelId, CancellationToken.None);
                    Assert.AreEqual(CadModelIdentityStatus.ActiveSaved, model.Status);
                    Assert.AreEqual(savedPath, model.CanonicalPath, true);
                    Assert.AreEqual(64, model.LastSavedSha256.Length);
                    var tree = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = CadCommandNames.GetFeatureTree,
                        Parameters = new JObject(), ExecutionId = jobId, ManagedModelId = modelId }, CancellationToken.None);
                    Assert.IsTrue(tree.Success, tree.Error?.Message);
                    Assert.IsTrue(tree.Data["features"] is JArray features && features.Count > 0);
                    TestContext?.WriteLine("Reopened feature tree: " + tree.Data);
                    var persisted = CadPlanDocumentReader.ReadPersisted(result.Revisions.Single().PlanJson).CandidateV2;
                    foreach (var sketch in persisted.Steps.Where(s => s.Command == CadCommandNames.CreateSketch))
                    {
                        Assert.IsTrue(sketch.OutputEntityId.HasValue && sketch.OutputEntityId.Value != Guid.Empty);
                        var binding = await repository.GetEntityBindingAsync(modelId, sketch.OutputEntityId.Value, model.ConfigurationKey, CancellationToken.None);
                        Assert.IsNotNull(binding);
                        Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                        Assert.IsTrue(binding.NativeReferenceBytes != null && binding.NativeReferenceBytes.Length > 0);
                        var resolved = await bridge.ResolveSketchReferenceAsync(modelId, sketch.OutputEntityId.Value, CancellationToken.None);
                        Assert.IsTrue(resolved.Success, "Reopened sketch " + sketch.OutputKey + ": " + resolved.Error?.Message);
                    }
                }
                finally
                {
                    var snapshot = jobId == Guid.Empty ? null : await repository.GetSnapshotAsync(jobId, CancellationToken.None);
                    var created = snapshot?.Commands.FirstOrDefault(c => c.CommandName == CadCommandNames.NewPart && c.Success);
                    Guid ownedId = Guid.Empty;
                    if (created != null) Guid.TryParse(JObject.Parse(created.ResultJson).Value<string>("modelId"), out ownedId);
                    await session.InvokeWithApplicationAsync(app =>
                    {
                        var sw = (SldWorks)app;
                        foreach (var doc in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                        {
                            doc.Extension.CustomPropertyManager[""].Get6("SolidWorksCadAgent.ModelId", false, out var text, out _, out _, out _);
                            if (ownedId == Guid.Empty || !Guid.TryParse(text, out var id) || id != ownedId) continue;
                            Assert.IsFalse(IsOriginal(doc, original), "Never close the original document.");
                            sw.CloseDoc(doc.GetTitle());
                        }
                        if (original != null)
                        {
                            Assert.IsTrue((sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>().Any(d => IsOriginal(d, original)));
                            int errors = 0;
                            var restored = sw.ActivateDoc3(original.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
                            Assert.IsNotNull(restored);
                            Assert.IsTrue(IsOriginal(restored, original));
                            Assert.AreEqual(original.Dirty, restored.GetSaveFlag());
                        }
                        return true;
                    }, CancellationToken.None);
                    TestContext?.WriteLine("Retained native acceptance evidence at " + workspace);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies for native v2 acceptance.");
#endif
        }

        private static JObject NewPlan() => new JObject { ["planVersion"] = 2,
            ["steps"] = new JArray(Step("new", "NewPart", new { })) };
        private static JObject Step(string key, string command, object parameters) => new JObject {
            ["stepKey"] = key, ["command"] = command, ["operationVersion"] = 1, ["parameters"] = JObject.FromObject(parameters) };
        private static void Group(JObject plan, string primitive, object profile, object featureParameters)
        {
            var steps = (JArray)plan["steps"];
            int number = (steps.Count - 1) / 4 + 1;
            var sketch = Step("sketch" + number, "CreateSketch", new { plane = "Top Plane" });
            sketch["outputKey"] = "profile" + number;
            steps.Add(sketch);
            steps.Add(Step("primitive" + number, primitive, profile));
            steps.Add(Step("exit" + number, "ExitSketch", new { }));
            var feature = Step("feature" + number, number == 1 ? "Extrude" : "CutExtrude", featureParameters);
            feature["operationVersion"] = 2;
            feature["inputs"] = new JObject { ["profileSketch"] = new JObject { ["kind"] = "Sketch", ["outputKey"] = "profile" + number } };
            steps.Add(feature);
        }
#if SOLIDWORKS_INTEROP
        private sealed class FixedPlanner : IVersionedCadPlanningProvider
        {
            private readonly string _json;
            public FixedPlanner(string json) { _json = json; }
            public Task<string> PlanDocumentAsync(CadPlanningRequest request, CancellationToken token) => Task.FromResult(_json);
            public Task<CadPlanningResult> PlanAsync(CadPlanningRequest request, CancellationToken token) =>
                throw new AssertFailedException("Native v2 acceptance must use versioned planning.");
        }
        private sealed class Original { public string Title, Path; public bool Dirty; }
        private static bool IsOriginal(ModelDoc2 doc, Original original) => original != null && doc.GetTitle() == original.Title && doc.GetPathName() == original.Path;
#endif
    }
}
