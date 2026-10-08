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
    [TestCategory("PrismaticCapability")]
    public class PrismaticCapabilityTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        [Timeout(180000)]
        public async Task RotatedSlotThroughCutAndHexagonalBlindPocket_SaveReopenPreservingOriginalDocument()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            await WithOwnedPartAsync("slot-and-hex-pocket", async (bridge, session) =>
            {
                await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                await Execute(bridge, "AddRectangle", new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 100.0, heightMm = 60.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "Extrude", new { depthMm = 10.0 });
                await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                await Execute(bridge, "AddSlot", new { centerXmm = -20.0, centerYmm = 0.0, lengthMm = 30.0, widthMm = 10.0, angleDegrees = 30.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "CutExtrude", new { endCondition = "ThroughAll" });
                await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                await Execute(bridge, "AddRegularPolygon", new { centerXmm = 20.0, centerYmm = 0.0, sides = 6, diameterMm = 16.0, angleDegrees = 0.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "CutExtrude", new { endCondition = "Blind", depthMm = 3.0 });
            }, async (bridge, session) =>
            {
                var slotArea = 10.0 * (30.0 - 10.0) + Math.PI * 5.0 * 5.0;
                var hexArea = 6.0 / 2.0 * 8.0 * 8.0 * Math.Sin(2.0 * Math.PI / 6.0);
                var evidence = await VerifyPhysicalGeometry(bridge, session, 100.0 * 60.0 * 10.0 - slotArea * 10.0 - hexArea * 3.0, new[] { 10.0, 60.0, 100.0 }, 2);
                var slot = evidence.Profiles.SingleOrDefault(p => p.ArcCenters.Count == 2 && p.LineStarts.Count == 2);
                Assert.IsNotNull(slot, "The native consumed slot sketch must contain two lines and two circular arcs.");
                foreach (var radius in slot.ArcRadiiMm) Assert.AreEqual(5.0, radius, 0.001);
                foreach (var length in slot.LineLengthsMm) Assert.AreEqual(20.0, length, 0.001, "Slot length is total tip-to-tip length, not arc-center separation.");
                var displacement = 10.0 * Math.Cos(Math.PI / 6.0);
                AssertContainsPoint(slot.ArcCenters, -20.0 - displacement, -5.0);
                AssertContainsPoint(slot.ArcCenters, -20.0 + displacement, 5.0);
                AssertPolygon(evidence, 20.0, 0.0, 6, 8.0, 0.0);
            });
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run native capability acceptance.");
#endif
        }

        [TestMethod]
        [Timeout(180000)]
        public async Task RotatedRegularHexagonBoss_SaveReopenPreservingOriginalDocument()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            await WithOwnedPartAsync("hexagon-boss", async (bridge, session) =>
            {
                await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                await Execute(bridge, "AddRegularPolygon", new { centerXmm = 0.0, centerYmm = 0.0, sides = 6, diameterMm = 16.0, angleDegrees = 15.0 });
                await Execute(bridge, "ExitSketch", new { });
                await Execute(bridge, "Extrude", new { depthMm = 5.0 });
            }, async (bridge, session) =>
            {
                var area = 6.0 / 2.0 * 8.0 * 8.0 * Math.Sin(2.0 * Math.PI / 6.0);
                var extent = 16.0 * Math.Cos(Math.PI / 12.0);
                var evidence = await VerifyPhysicalGeometry(bridge, session, area * 5.0, new[] { 5.0, extent, extent }, 0);
                AssertPolygon(evidence, 0.0, 0.0, 6, 8.0, 15.0);
            });
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run native capability acceptance.");
#endif
        }

        private static void RequireOptIn()
        {
            if (System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_PRISMATIC_TESTS") != "1")
                Assert.Inconclusive("Set SOLIDWORKS_RUN_PRISMATIC_TESTS=1 and filter PrismaticCapabilityTests to opt into native SOLIDWORKS 2020 acceptance.");
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

        private static async Task<NativeEvidence> VerifyPhysicalGeometry(SolidWorksBridgeFacade bridge, SolidWorksSession session, double expectedVolume, double[] expectedBounds, int expectedCuts)
        {
            await Execute(bridge, "Rebuild", new { });
            Assert.AreEqual(1, (await Execute(bridge, "GetBodyCount", new { })).Data.Value<int>("bodyCount"));
            var bounds = (await Execute(bridge, "GetBoundingBox", new { })).Data;
            var actualBounds = new[] { bounds.Value<double>("sizeXmm"), bounds.Value<double>("sizeYmm"), bounds.Value<double>("sizeZmm") }.OrderBy(value => value).ToArray();
            for (var axis = 0; axis < 3; axis++) Assert.AreEqual(expectedBounds[axis], actualBounds[axis], 0.02, "Native physical extents must match, independent of reference-plane axis orientation.");
            var rebuild = (await Execute(bridge, "GetRebuildErrors", new { })).Data;
            Assert.IsTrue(rebuild.Value<bool>("rebuilt")); Assert.IsFalse(rebuild.Value<bool>("hasErrors"));
            var evidence = await session.InvokeWithApplicationAsync(InspectNative, CancellationToken.None);
            Console.WriteLine("Native feature type audit: " + string.Join(", ", evidence.FeatureTypeAudit));
            Console.WriteLine("Native sketch profiles: " + string.Join(", ", evidence.Profiles.Select(profile => profile.FeatureName + " lines=" + profile.LineStarts.Count + " arcs=" + profile.ArcCenters.Count)));
            Assert.AreEqual(1, evidence.BodyCount);
            Assert.AreEqual(expectedVolume, evidence.VolumeMm3, 0.05, "Native solid volume must reflect the exact capsule area and blind pocket depth, not sketch-only geometry or through-all substitution.");
            Assert.AreEqual(1, evidence.FeatureTypes.Count(type => type == "Extrusion" || type == "Boss"), "Require a native boss/extrusion feature. Native types: " + string.Join(", ", evidence.FeatureTypeAudit));
            Assert.AreEqual(expectedCuts, evidence.FeatureTypes.Count(type => type == "Cut"), "Require native cut-extrude features. Native types: " + string.Join(", ", evidence.FeatureTypeAudit));
            return evidence;
        }
        private static NativeEvidence InspectNative(object app)
        {
            var model = (ModelDoc2)((SldWorks)app).ActiveDoc;
            var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            Assert.IsNotNull(bodies); Assert.AreEqual(1, bodies.Length);
            var mass = ((Body2)bodies[0]).GetMassProperties(1.0) as double[];
            Assert.IsNotNull(mass); Assert.IsTrue(mass.Length >= 4);
            var evidence = new NativeEvidence { BodyCount = bodies.Length, VolumeMm3 = mass[3] * 1e9 };
            var visited = new HashSet<string>();
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature) InspectFeature(feature, evidence, visited);
            return evidence;
        }
        private static void InspectFeature(Feature feature, NativeEvidence evidence, HashSet<string> visited)
        {
            if (!visited.Add(feature.Name)) return;
            var reportedType = feature.GetTypeName2();
            // SOLIDWORKS documents ICE as an Instant3D wrapper; GetTypeName returns its underlying feature type.
            // https://help.solidworks.com/2017/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeature~GetTypeName2.html
            var nativeType = reportedType == "ICE" ? feature.GetTypeName() : reportedType;
            evidence.FeatureTypeAudit.Add(feature.Name + ": " + reportedType + (reportedType == nativeType ? "" : " -> " + nativeType));
            evidence.FeatureTypes.Add(nativeType);
            if ((nativeType == "ProfileFeature" || nativeType == "3DProfileFeature") && feature.GetSpecificFeature2() is Sketch sketch)
            {
                var profile = new SketchProfile { FeatureName = feature.Name };
                foreach (var value in sketch.GetSketchSegments() as object[] ?? Array.Empty<object>())
                {
                    var segment = (SketchSegment)value;
                    // Centre rectangles include construction diagonals; only profile edges define the solid.
                    if (segment.ConstructionGeometry) continue;
                    if (segment.GetType() == (int)swSketchSegments_e.swSketchLINE)
                    {
                        var line = (SketchLine)value; var start = (SketchPoint)line.GetStartPoint2(); var end = (SketchPoint)line.GetEndPoint2();
                        profile.LineStarts.Add(new[] { start.X * 1000.0, start.Y * 1000.0 });
                        profile.LineLengthsMm.Add(Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) * 1000.0);
                    }
                    else if (segment.GetType() == (int)swSketchSegments_e.swSketchARC)
                    {
                        var arc = (SketchArc)value; var center = (SketchPoint)arc.GetCenterPoint2();
                        profile.ArcCenters.Add(new[] { center.X * 1000.0, center.Y * 1000.0 }); profile.ArcRadiiMm.Add(arc.GetRadius() * 1000.0);
                    }
                }
                evidence.Profiles.Add(profile);
            }
            for (var child = feature.GetFirstSubFeature() as Feature; child != null; child = child.GetNextSubFeature() as Feature) InspectFeature(child, evidence, visited);
        }
        private static void AssertPolygon(NativeEvidence evidence, double centerX, double centerY, int sides, double radius, double angleDegrees)
        {
            var polygon = evidence.Profiles.SingleOrDefault(profile => profile.LineStarts.Count == sides && profile.ArcCenters.Count == 0);
            Assert.IsNotNull(polygon, "Require a native closed regular polygon sketch with the requested number of sides.");
            for (var vertex = 0; vertex < sides; vertex++)
            {
                var angle = angleDegrees * Math.PI / 180.0 + vertex * 2.0 * Math.PI / sides;
                AssertContainsPoint(polygon.LineStarts, centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle));
            }
        }
        private static void AssertContainsPoint(IEnumerable<double[]> points, double x, double y) => Assert.IsTrue(points.Any(point => Math.Abs(point[0] - x) < 0.001 && Math.Abs(point[1] - y) < 0.001), "Missing native sketch point at (" + x + ", " + y + ") mm.");
        private sealed class OriginalDocument { public string Title; public string Path; public bool SaveFlag; }
        private sealed class NativeEvidence { public int BodyCount; public double VolumeMm3; public List<string> FeatureTypes = new List<string>(); public List<string> FeatureTypeAudit = new List<string>(); public List<SketchProfile> Profiles = new List<SketchProfile>(); }
        private sealed class SketchProfile { public string FeatureName; public List<double[]> LineStarts = new List<double[]>(); public List<double> LineLengthsMm = new List<double>(); public List<double[]> ArcCenters = new List<double[]>(); public List<double> ArcRadiiMm = new List<double>(); }
#endif
        private static async Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string command, object parameters)
        {
            var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = command, Parameters = JObject.FromObject(parameters) }, CancellationToken.None);
            Assert.IsTrue(result.Success, command + ": " + result.Error?.Code + " " + result.Error?.Message);
            return result;
        }
    }
}
