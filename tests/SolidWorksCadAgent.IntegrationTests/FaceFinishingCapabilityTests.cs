using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
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
    [TestCategory("FaceFinishingCapability")]
    public class FaceFinishingCapabilityTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        [Timeout(180000)]
        public async Task OppositeFaceBlindPockets_UseWorldXZCoordinatesAndCutIntoBody_SaveReopen()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            await WithOwnedPartAsync("opposite-face-pockets", async (bridge, session) =>
            {
                await BuildPlate(bridge);
                await VerifyPlate(bridge, session, 60000.0, 0);
                await Execute(bridge, "CreateSketchOnFace", new { normalAxis = "Y", side = "Max" });
                await Execute(bridge, "AddCircle", new { centerXmm = 15.0, centerYmm = 8.0, diameterMm = 12.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "CutExtrude", new { endCondition = "Blind", depthMm = 3.0, direction = "IntoBody" });
                await Execute(bridge, "CreateSketchOnFace", new { normalAxis = "Y", side = "Min" });
                await Execute(bridge, "AddCircle", new { centerXmm = -15.0, centerYmm = -8.0, diameterMm = 8.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "CutExtrude", new { endCondition = "Blind", depthMm = 2.0, direction = "IntoBody" });
            }, async (bridge, session) =>
            {
                var expected = 60000.0 - Math.PI * 6.0 * 6.0 * 3.0 - Math.PI * 4.0 * 4.0 * 2.0;
                var evidence = await VerifyPlate(bridge, session, expected, 2);
                // Real body edges, rather than circles merely present in sketches, establish face placement and pocket floor depths.
                AssertCircle(evidence, 15.0, 10.0, 8.0, 6.0, "Max-Y pocket opening");
                AssertCircle(evidence, 15.0, 7.0, 8.0, 6.0, "Max-Y pocket floor after a 3 mm inward cut");
                AssertCircle(evidence, -15.0, 0.0, -8.0, 4.0, "Min-Y pocket opening");
                AssertCircle(evidence, -15.0, 2.0, -8.0, 4.0, "Min-Y pocket floor after a 2 mm inward cut");
            });
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Installed SOLIDWORKS interop is required for native face/finishing acceptance.");
#endif
        }

        [TestMethod]
        [Timeout(180000)]
        public async Task OuterFaceFilletAndChamfer_PreserveInnerThroughHole_SaveReopen()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            double? finishedVolume = null;
            var stockVolume = 60000.0 - Math.PI * 10.0 * 10.0 * 10.0;
            await WithOwnedPartAsync("outer-fillet-chamfer", async (bridge, session) =>
            {
                await BuildPlate(bridge);
                await VerifyPlate(bridge, session, 60000.0, 0);
                await Execute(bridge, "CreateSketchOnFace", new { normalAxis = "Y", side = "Max" });
                await Execute(bridge, "AddCircle", new { centerXmm = 0.0, centerYmm = 0.0, diameterMm = 20.0 });
                await Execute(bridge, "ExitSketch", new { });
                // The face-bound default must choose inward direction even when direction is omitted.
                await Execute(bridge, "CutExtrude", new { endCondition = "ThroughAll" });
                var stock = await VerifyPlate(bridge, session, stockVolume, 1);
                AssertThroughHoleUnchanged(stock);
                await Execute(bridge, "FilletEdges", new { normalAxis = "Y", side = "Max", radiusMm = 1.0 });
                await Execute(bridge, "ChamferEdges", new { normalAxis = "Y", side = "Min", distanceMm = 1.0 });
            }, async (bridge, session) =>
            {
                var evidence = await VerifyPlate(bridge, session, finishedVolume, 1);
                var removed = stockVolume - evidence.VolumeMm3;
                Assert.IsTrue(removed > 1.0 && removed < 1000.0, "1 mm outer finishing must remove a small positive native volume, not merely add a feature label. Removed mm³: " + removed);
                Assert.AreEqual(1, evidence.Types.Count(type => type == "Fillet"), "Require one native fillet. " + string.Join(", ", evidence.TypeAudit));
                Assert.AreEqual(1, evidence.Types.Count(type => type == "Chamfer"), "Require one native chamfer. " + string.Join(", ", evidence.TypeAudit));
                AssertThroughHoleUnchanged(evidence);
                if (!finishedVolume.HasValue) finishedVolume = evidence.VolumeMm3;
            });
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Installed SOLIDWORKS interop is required for native face/finishing acceptance.");
#endif
        }

        private static void RequireOptIn()
        {
            if (System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_FACE_FINISHING_TESTS") != "1")
                Assert.Inconclusive("Set SOLIDWORKS_RUN_FACE_FINISHING_TESTS=1 and filter FaceFinishingCapabilityTests to opt into native SOLIDWORKS 2020 acceptance.");
        }
#if SOLIDWORKS_INTEROP
        private async Task WithOwnedPartAsync(string name, Func<SolidWorksBridgeFacade, SolidWorksSession, Task> build, Func<SolidWorksBridgeFacade, SolidWorksSession, Task> verify)
        {
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", Guid.NewGuid().ToString("N"));
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace)))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, "Start SOLIDWORKS before opting into these tests. " + status.ErrorMessage);
                Assert.IsNotNull(status.RuntimeInfo);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear, "These acceptance tests target installed SOLIDWORKS 2020.");
                var original = await session.InvokeWithApplicationAsync(app => CaptureOriginal((SldWorks)app), CancellationToken.None);
                var ownedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string savedPath = null;
                try
                {
                    Directory.CreateDirectory(workspace);
                    var created = await Execute(bridge, "NewPart", new { });
                    var title = created.Data.Value<string>("documentTitle");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(title));
                    Assert.AreNotEqual(original?.Title, title, "NewPart must create a distinct owned document.");
                    ownedTitles.Add(title);
                    await build(bridge, session);
                    await verify(bridge, session);
                    var save = await Execute(bridge, "SavePart", new { path = name + ".sldprt", allowOverwrite = false });
                    savedPath = save.Data.Value<string>("path");
                    Assert.AreEqual(Path.GetFullPath(Path.Combine(workspace, name + ".sldprt")), Path.GetFullPath(savedPath), true);
                    Assert.IsTrue(File.Exists(savedPath));
                    ownedTitles.Add(await session.InvokeWithApplicationAsync(app => ((ModelDoc2)((SldWorks)app).ActiveDoc).GetTitle(), CancellationToken.None));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    await Execute(bridge, "CloseDocument", new { });
                    var reopened = await Execute(bridge, "OpenPart", new { path = name + ".sldprt" });
                    ownedTitles.Add(reopened.Data.Value<string>("documentTitle"));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    await verify(bridge, session);
                    TestContext?.WriteLine("Native capability artifact retained: " + savedPath);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(app =>
                    {
                        var sw = (SldWorks)app;
                        try
                        {
                            // Only this test's generated unsaved title or isolated saved path may be closed.
                            foreach (var document in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                            {
                                var title = document.GetTitle(); var path = document.GetPathName();
                                var isOwned = ownedTitles.Contains(title) && (string.IsNullOrEmpty(path) || IsInside(path, workspace));
                                if (!isOwned) continue;
                                Assert.IsFalse(IsOriginal(document, original), "Cleanup must never close the user's original document.");
                                sw.CloseDoc(title);
                            }
                            Assert.IsFalse((sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>().Any(document =>
                                ownedTitles.Contains(document.GetTitle()) && (string.IsNullOrEmpty(document.GetPathName()) || IsInside(document.GetPathName(), workspace))), "All owned test documents must be closed.");
                        }
                        finally { RestoreAndAssertOriginal(sw, original); }
                        return true;
                    }, CancellationToken.None);
                }
            }
        }
        private static OriginalDocument CaptureOriginal(SldWorks sw)
        {
            var model = sw.ActiveDoc as ModelDoc2;
            return model == null ? null : new OriginalDocument { Title = model.GetTitle(), Path = model.GetPathName(), SaveFlag = model.GetSaveFlag() };
        }
        private static bool IsOriginal(ModelDoc2 model, OriginalDocument original) => original != null && model.GetTitle() == original.Title && model.GetPathName() == original.Path;
        private static bool IsInside(string path, string workspace) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static void RestoreAndAssertOriginal(SldWorks sw, OriginalDocument original)
        {
            if (original == null) return;
            var open = (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>().SingleOrDefault(model => IsOriginal(model, original));
            Assert.IsNotNull(open, "The user's original document must remain open, with its original title and path.");
            Assert.AreEqual(original.Path, open.GetPathName());
            Assert.AreEqual(original.SaveFlag, open.GetSaveFlag(), "The user's original document save flag must not change.");
            var errors = 0;
            var activated = sw.ActivateDoc3(original.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
            Assert.IsNotNull(activated, "Restore the original active document without rebuilding it.");
            Assert.IsTrue(IsOriginal(activated, original));
            Assert.AreEqual(original.SaveFlag, activated.GetSaveFlag());
        }
        private static Task<bool> AssertActiveOwned(SolidWorksSession session, HashSet<string> titles, string workspace, OriginalDocument original) => session.InvokeWithApplicationAsync(app =>
        {
            var model = (ModelDoc2)((SldWorks)app).ActiveDoc;
            Assert.IsNotNull(model);
            Assert.IsTrue(titles.Contains(model.GetTitle()) && IsInside(model.GetPathName(), workspace), "Close/reopen verification must target only the generated saved part.");
            Assert.IsFalse(IsOriginal(model, original));
            return true;
        }, CancellationToken.None);

        private static async Task BuildPlate(SolidWorksBridgeFacade bridge)
        {
            await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
            await Execute(bridge, "AddRectangle", new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 100.0, heightMm = 60.0 });
            await Execute(bridge, "ExitSketch", new { });
            await Execute(bridge, "Extrude", new { depthMm = 10.0 });
        }
        private static async Task<FaceEvidence> VerifyPlate(SolidWorksBridgeFacade bridge, SolidWorksSession session, double? expectedVolume, int cuts)
        {
            await Execute(bridge, "Rebuild", new { });
            Assert.AreEqual(1, (await Execute(bridge, "GetBodyCount", new { })).Data.Value<int>("bodyCount"));
            var rebuild = (await Execute(bridge, "GetRebuildErrors", new { })).Data;
            Assert.IsTrue(rebuild.Value<bool>("rebuilt")); Assert.IsFalse(rebuild.Value<bool>("hasErrors"));
            var evidence = await session.InvokeWithApplicationAsync(InspectNative, CancellationToken.None);
            Assert.AreEqual(1, evidence.BodyCount);
            // Assert actual global orientation; these face-coordinate tests must not silently permute axes.
            var expectedBounds = new[] { -50.0, 0.0, -30.0, 50.0, 10.0, 30.0 };
            Assert.IsNotNull(evidence.BoundsMm); Assert.AreEqual(6, evidence.BoundsMm.Length);
            for (var index = 0; index < 6; index++) Assert.AreEqual(expectedBounds[index], evidence.BoundsMm[index], 0.02, "Expected plate spans X=±50, Z=±30 and positive boss Y=0..10 mm. Native bounds: " + string.Join(", ", evidence.BoundsMm));
            if (expectedVolume.HasValue) Assert.AreEqual(expectedVolume.Value, evidence.VolumeMm3, 0.05, "Require exact native material removal and unchanged geometry after reopening.");
            Assert.AreEqual(1, evidence.Types.Count(type => type == "Extrusion" || type == "Boss"), "Require a native plate boss. " + string.Join(", ", evidence.TypeAudit));
            Assert.AreEqual(cuts, evidence.Types.Count(type => type == "Cut"), "Require native cut features. " + string.Join(", ", evidence.TypeAudit));
            Console.WriteLine("Face/finishing native volume mm³: " + evidence.VolumeMm3 + "; feature types: " + string.Join(", ", evidence.TypeAudit));
            Console.WriteLine("Native circular body edges: " + string.Join("; ", evidence.Circles.Select(circle => "(" + circle.X + "," + circle.Y + "," + circle.Z + ") r=" + circle.Radius)));
            return evidence;
        }
        private static FaceEvidence InspectNative(object application)
        {
            var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
            var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            Assert.IsNotNull(bodies); Assert.AreEqual(1, bodies.Length);
            var body = (Body2)bodies[0]; var mass = body.GetMassProperties(1.0) as double[];
            Assert.IsNotNull(mass); Assert.IsTrue(mass.Length >= 4);
            var bounds = body.GetBodyBox() as double[];
            var result = new FaceEvidence { BodyCount = bodies.Length, VolumeMm3 = mass[3] * 1e9, BoundsMm = bounds?.Select(value => value * 1000.0).ToArray() };
            foreach (var value in body.GetEdges() as object[] ?? Array.Empty<object>())
            {
                var curve = ((Edge)value).GetCurve() as Curve; if (curve == null || !curve.IsCircle()) continue;
                var circle = curve.CircleParams as double[]; Assert.IsNotNull(circle); Assert.IsTrue(circle.Length >= 7);
                result.Circles.Add(new CircleEvidence { X = circle[0] * 1000.0, Y = circle[1] * 1000.0, Z = circle[2] * 1000.0, Radius = circle[6] * 1000.0, NormalY = circle[4] });
            }
            foreach (var value in body.GetFaces() as object[] ?? Array.Empty<object>())
            {
                var face = (Face2)value; var surface = face.GetSurface() as Surface; if (surface == null || !surface.IsCylinder()) continue;
                var cylinder = surface.CylinderParams as double[]; Assert.IsNotNull(cylinder); Assert.IsTrue(cylinder.Length >= 7);
                if (Math.Abs(cylinder[0]) < 0.000001 && Math.Abs(cylinder[2]) < 0.000001 && Math.Abs(Math.Abs(cylinder[4]) - 1.0) < 0.000001 && Math.Abs(cylinder[6] - 0.01) < 0.000001)
                    result.CentralHoleCylinderAreaMm2 += face.GetArea() * 1e6;
            }
            var visited = new HashSet<string>();
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature) InspectFeatureTypes(feature, result, visited);
            return result;
        }
        private static void InspectFeatureTypes(Feature feature, FaceEvidence result, HashSet<string> visited)
        {
            if (!visited.Add(feature.Name)) return;
            var reported = feature.GetTypeName2(); var type = reported == "ICE" ? feature.GetTypeName() : reported;
            result.Types.Add(type); result.TypeAudit.Add(feature.Name + ": " + reported + (type == reported ? "" : " -> " + type));
            for (var child = feature.GetFirstSubFeature() as Feature; child != null; child = child.GetNextSubFeature() as Feature) InspectFeatureTypes(child, result, visited);
        }
        private static void AssertCircle(FaceEvidence evidence, double x, double y, double z, double radius, string purpose)
        {
            Assert.IsTrue(evidence.Circles.Any(circle => Math.Abs(circle.X - x) < 0.001 && Math.Abs(circle.Y - y) < 0.001 && Math.Abs(circle.Z - z) < 0.001 && Math.Abs(circle.Radius - radius) < 0.001 && Math.Abs(Math.Abs(circle.NormalY) - 1.0) < 0.000001), purpose + " must be a physical circular body edge centered at world (" + x + "," + y + "," + z + ") mm, radius " + radius + " mm.");
        }
        private static void AssertThroughHoleUnchanged(FaceEvidence evidence)
        {
            AssertCircle(evidence, 0.0, 10.0, 0.0, 10.0, "Unfilleted top inner hole rim");
            AssertCircle(evidence, 0.0, 0.0, 0.0, 10.0, "Unchamfered bottom inner hole rim");
            Assert.AreEqual(2.0 * Math.PI * 10.0 * 10.0, evidence.CentralHoleCylinderAreaMm2, 0.05, "The Ø20 inner cylindrical wall must keep its full 10 mm height; inner hole edges must never be selected for finishing.");
        }
        private sealed class OriginalDocument { public string Title; public string Path; public bool SaveFlag; }
        private sealed class FaceEvidence { public int BodyCount; public double VolumeMm3; public double[] BoundsMm; public double CentralHoleCylinderAreaMm2; public List<string> Types = new List<string>(); public List<string> TypeAudit = new List<string>(); public List<CircleEvidence> Circles = new List<CircleEvidence>(); }
        private sealed class CircleEvidence { public double X; public double Y; public double Z; public double Radius; public double NormalY; }
#endif
        private static async Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string command, object parameters)
        {
            var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = command, Parameters = JObject.FromObject(parameters) }, CancellationToken.None);
            Assert.IsTrue(result.Success, command + ": " + result.Error?.Code + " " + result.Error?.Message);
            return result;
        }
    }
}
