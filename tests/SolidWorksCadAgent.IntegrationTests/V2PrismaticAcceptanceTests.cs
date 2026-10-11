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

        [TestMethod, Timeout(240000)]
        public Task FrontPlaneSemicircleBoss_ApprovedJobVerifiesNativeContourAfterReopen()
        {
            var plan = NewPlan();
            ContourGroup(plan, new[]
            {
                Step("line1", "AddLine", new { startXmm = 17.0, startYmm = 11.0, endXmm = -3.0, endYmm = 11.0 }),
                Step("arc1", "AddArc", new { centerXmm = 7.0, centerYmm = 11.0, startXmm = -3.0, startYmm = 11.0,
                    endXmm = 17.0, endYmm = 11.0, clockwise = true })
            }, new { depthMm = 5.0 }, "Front Plane");
            return RunAsync("front-semicircle", plan, 250 * Math.PI,
                new[] { -3.0, 17.0, 11.0, 21.0, 0.0, 5.0 }, 1, "Front Plane", false, true);
        }

        [TestMethod, Timeout(300000)]
        public Task RectangularPlateWithSemicircleCut_ApprovedJobVerifiesContourAndReopens()
        {
            var plan = NewPlan();
            Group(plan, "AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 },
                new { depthMm = 13.0 });
            ContourGroup(plan, new[]
            {
                Step("line2", "AddLine", new { startXmm = -5.0, startYmm = 12.0, endXmm = -11.0, endYmm = 12.0 }),
                Step("arc2", "AddArc", new { centerXmm = -8.0, centerYmm = 12.0, startXmm = -11.0, startYmm = 12.0,
                    endXmm = -5.0, endYmm = 12.0, clockwise = true })
            }, new { endCondition = "ThroughAll" });
            return RunAsync("semicircle-cut", plan, 31200 - 58.5 * Math.PI,
                new[] { -23.0, 37.0, 0.0, 13.0, -31.0, 9.0 }, 2, "Top Plane", false, true);
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

        [DataTestMethod]
        [DataRow("Front Plane", "front-plane", -23.0, 37.0, -9.0, 31.0, 0.0, 13.0)]
        [DataRow("Right Plane", "right-plane", 0.0, 13.0, -9.0, 31.0, -37.0, 23.0)]
        [DataRow("Top Plane", "top-plane", -23.0, 37.0, 0.0, 13.0, -31.0, 9.0)]
        public Task OffsetBossWithThroughAndBlindCuts_ApprovedJobVerifiesPlaneFrameAndReopensSketches(
            string plane, string name, double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            var plan = NewPlan();
            Group(plan, "AddRectangle", new { centerXmm = 7.0, centerYmm = 11.0, widthMm = 60.0, heightMm = 40.0 },
                new { depthMm = 13.0 }, plane);
            Group(plan, "AddCircle", new { centerXmm = -8.0, centerYmm = 12.0, diameterMm = 6.0 },
                new { endCondition = "ThroughAll" }, plane);
            Group(plan, "AddSlot", new { centerXmm = 17.0, centerYmm = -1.0, lengthMm = 12.0, widthMm = 4.0, angleDegrees = 23.0 },
                new { endCondition = "Blind", depthMm = 4.0 }, plane);
            return RunAsync(name, plan, 31072.0 - 133.0 * Math.PI,
                new[] { minX, maxX, minY, maxY, minZ, maxZ }, 3, plane, true);
        }

        private async Task RunAsync(string name, JObject candidate, double expectedVolume, double[] bounds, int groups,
            string plane = "Top Plane", bool assertAsymmetricPlaneSketches = false, bool assertClosedContourSketch = false)
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
                    Assert.AreEqual(((JArray)candidate["steps"]).Count, result.Commands.Count);
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
                    var frame = CadStandardPlaneFrame.FromPlaneName(plane);
                    var origin = frame.ToModel(0, 0, 0);
                    var bossDepth = candidate["steps"].Values<JObject>()
                        .First(step => step.Value<string>("command") == CadCommandNames.Extrude)
                        .Value<JObject>("parameters").Value<double>("depthMm");
                    var depthEnd = frame.ToModel(0, 0, bossDepth);
                    var expectedDepth = Math.Abs(depthEnd.Xmm - origin.Xmm) + Math.Abs(depthEnd.Ymm - origin.Ymm) + Math.Abs(depthEnd.Zmm - origin.Zmm);
                    var sizes = new[] { observed["bounds"].Value<double>("sizeXmm"), observed["bounds"].Value<double>("sizeYmm"),
                        observed["bounds"].Value<double>("sizeZmm") };
                    Assert.IsTrue(sizes.Any(size => Math.Abs(size - expectedDepth) < 0.02), "Boss depth must follow the selected plane normal.");
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
                        if (assertAsymmetricPlaneSketches)
                            await session.InvokeWithApplicationAsync(app =>
                            {
#if SOLIDWORKS_INTEROP
                                var native = (ModelDoc2)((SldWorks)app).ActiveDoc;
                                var nativeStatus = -1;
                                var objectRef = native.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes, out nativeStatus);
                                Assert.AreEqual((int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok, nativeStatus);
                                var nativeSketch = objectRef as Sketch ?? (objectRef as Feature)?.GetSpecificFeature2() as Sketch;
                                Assert.IsNotNull(nativeSketch, "The persisted sketch reference must resolve to its native sketch.");
                                if (sketch.OutputKey == "profile1") AssertSketchBounds(nativeSketch, -23, 37, -9, 31);
                                if (sketch.OutputKey == "profile2") AssertCircleCenter(nativeSketch, -8, 12);
                                return true;
#else
                                return false;
#endif
                            }, CancellationToken.None);
                        if (assertClosedContourSketch && (string)sketch.OutputKey == "profile" + (groups == 1 ? "1" : "2"))
                            await session.InvokeWithApplicationAsync(app =>
                            {
#if SOLIDWORKS_INTEROP
                                var native = (ModelDoc2)((SldWorks)app).ActiveDoc;
                                var nativeStatus = -1;
                                var objectRef = native.Extension.GetObjectByPersistReference3(binding.NativeReferenceBytes, out nativeStatus);
                                Assert.AreEqual((int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok, nativeStatus);
                                var nativeSketch = objectRef as Sketch ?? (objectRef as Feature)?.GetSpecificFeature2() as Sketch;
                                Assert.IsNotNull(nativeSketch, "The persisted contour sketch reference must resolve after reopen.");
                                AssertSemicircleSketch(nativeSketch, groups == 1 ? 7.0 : -8.0, groups == 1 ? 11.0 : 12.0,
                                    groups == 1 ? 10.0 : 3.0, groups == 1 ? 17.0 : -5.0, groups == 1 ? -3.0 : -11.0, -1);
                                return true;
#else
                                return false;
#endif
                            }, CancellationToken.None);
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
        private static void Group(JObject plan, string primitive, object profile, object featureParameters, string plane = "Top Plane")
        {
            var steps = (JArray)plan["steps"];
            int number = steps.Count(step => (string)step["command"] == CadCommandNames.CreateSketch) + 1;
            var sketch = Step("sketch" + number, "CreateSketch", new { plane });
            sketch["outputKey"] = "profile" + number;
            steps.Add(sketch);
            steps.Add(Step("primitive" + number, primitive, profile));
            steps.Add(Step("exit" + number, "ExitSketch", new { }));
            var feature = Step("feature" + number, number == 1 ? "Extrude" : "CutExtrude", featureParameters);
            feature["operationVersion"] = 2;
            feature["inputs"] = new JObject { ["profileSketch"] = new JObject { ["kind"] = "Sketch", ["outputKey"] = "profile" + number } };
            steps.Add(feature);
        }
        private static void ContourGroup(JObject plan, JObject[] segments, object featureParameters, string plane = "Top Plane")
        {
            var steps = (JArray)plan["steps"];
            int number = steps.Count(step => (string)step["command"] == CadCommandNames.CreateSketch) + 1;
            var sketch = Step("sketch" + number, "CreateSketch", new { plane });
            sketch["outputKey"] = "profile" + number;
            steps.Add(sketch);
            foreach (var segment in segments) steps.Add(segment);
            steps.Add(Step("exit" + number, "ExitSketch", new { }));
            var feature = Step("feature" + number, number == 1 ? "Extrude" : "CutExtrude", featureParameters);
            feature["operationVersion"] = 2;
            feature["inputs"] = new JObject { ["profileSketch"] = new JObject { ["kind"] = "Sketch", ["outputKey"] = "profile" + number } };
            steps.Add(feature);
        }
#if SOLIDWORKS_INTEROP
        private static void AssertSketchBounds(Sketch sketch, double minX, double maxX, double minY, double maxY)
        {
            var points = new System.Collections.Generic.List<Tuple<double, double>>();
            foreach (var segment in sketch.GetSketchSegments() as object[] ?? Array.Empty<object>())
            {
                var line = segment as SketchLine;
                if (line == null || ((SketchSegment)line).ConstructionGeometry) continue;
                var start = (SketchPoint)line.GetStartPoint2(); var end = (SketchPoint)line.GetEndPoint2();
                points.Add(Tuple.Create(start.X * 1000.0, start.Y * 1000.0));
                points.Add(Tuple.Create(end.X * 1000.0, end.Y * 1000.0));
            }
            Assert.AreEqual(8, points.Count, "The asymmetric base sketch must contain four native lines.");
            Assert.AreEqual(minX, points.Min(point => point.Item1), 0.001, "local sketch min X");
            Assert.AreEqual(maxX, points.Max(point => point.Item1), 0.001, "local sketch max X");
            Assert.AreEqual(minY, points.Min(point => point.Item2), 0.001, "local sketch min Y");
            Assert.AreEqual(maxY, points.Max(point => point.Item2), 0.001, "local sketch max Y");
        }

        private static void AssertCircleCenter(Sketch sketch, double expectedX, double expectedY)
        {
            var arcs = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>())
                .Cast<SketchSegment>().Where(segment => segment.GetType() == (int)swSketchSegments_e.swSketchARC).ToArray();
            Assert.AreEqual(1, arcs.Length, "The through cut sketch must retain one native circle.");
            var center = (SketchPoint)((SketchArc)arcs[0]).GetCenterPoint2();
            Assert.AreEqual(expectedX, center.X * 1000.0, 0.001, "circle local X");
            Assert.AreEqual(expectedY, center.Y * 1000.0, 0.001, "circle local Y");
        }

        private static void AssertSemicircleSketch(Sketch sketch, double centerX, double centerY, double radius,
            double lineStartX, double lineEndX, int rotationDirection)
        {
            var segments = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>()).Cast<SketchSegment>().ToArray();
            Assert.AreEqual(2, segments.Length, "The saved profile must contain the original line and arc entities.");
            var line = segments.Single(segment => segment.GetType() == (int)swSketchSegments_e.swSketchLINE) as SketchLine;
            var arc = segments.Single(segment => segment.GetType() == (int)swSketchSegments_e.swSketchARC) as SketchArc;
            Assert.IsNotNull(line); Assert.IsNotNull(arc);
            var start = (SketchPoint)line.GetStartPoint2(); var end = (SketchPoint)line.GetEndPoint2();
            Assert.AreEqual(lineStartX, start.X * 1000.0, 0.001); Assert.AreEqual(centerY, start.Y * 1000.0, 0.001);
            Assert.AreEqual(lineEndX, end.X * 1000.0, 0.001); Assert.AreEqual(centerY, end.Y * 1000.0, 0.001);
            var arcCenter = (SketchPoint)arc.GetCenterPoint2(); var arcStart = (SketchPoint)arc.GetStartPoint2();
            var arcEnd = (SketchPoint)arc.GetEndPoint2();
            Assert.AreEqual(centerX, arcCenter.X * 1000.0, 0.001); Assert.AreEqual(centerY, arcCenter.Y * 1000.0, 0.001);
            Assert.AreEqual(lineEndX, arcStart.X * 1000.0, 0.001, "The native arc must preserve its ordered start point.");
            Assert.AreEqual(lineStartX, arcEnd.X * 1000.0, 0.001, "The native arc must preserve its ordered end point.");
            Assert.AreEqual(rotationDirection, arc.GetRotationDir(), "The saved native arc must preserve the requested clockwise direction.");
            Assert.AreEqual(radius, Math.Sqrt(Math.Pow((arcStart.X - arcCenter.X) * 1000.0, 2) +
                Math.Pow((arcStart.Y - arcCenter.Y) * 1000.0, 2)), 0.001);
        }

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
