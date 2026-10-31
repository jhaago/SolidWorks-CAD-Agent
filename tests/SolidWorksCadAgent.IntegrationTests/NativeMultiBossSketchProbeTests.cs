using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
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
    public class NativeMultiBossSketchProbeTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod, Timeout(180000)]
        public async Task RectangleHandlerProbeReportsSketchesAfterEachBoss()
        {
            if (System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS") != "1")
                Assert.Inconclusive("Set SOLIDWORKS_RUN_REFERENCE_TESTS=1 to run the disposable native geometry probe.");
            Assert.AreEqual("2020", System.Environment.GetEnvironmentVariable("SOLIDWORKS_EXPECTED_YEAR"));
#if SOLIDWORKS_INTEROP
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", "native-multi-boss-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace)))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var original = await session.InvokeWithApplicationAsync(app => CaptureOriginal((SldWorks)app), CancellationToken.None);
                string createdTitle = null;
                try
                {
                    createdTitle = (await Execute(bridge, CadCommandNames.NewPart, new { })).Data.Value<string>("documentTitle");
                    await CreateRectangleBoss(bridge, session, 1.0, 2.0, 20.0, 10.0, 5.0);
                    var afterFirstBoss = await session.InvokeWithApplicationAsync(app => Inspect((ModelDoc2)((SldWorks)app).ActiveDoc), CancellationToken.None);
                    TestContext?.WriteLine("After first boss: " + afterFirstBoss);
                    Assert.AreEqual(1, afterFirstBoss.Profiles.Count);
                    Assert.AreEqual(4, afterFirstBoss.Profiles[0].LineCount);
                    Assert.AreEqual(2, afterFirstBoss.Profiles[0].ConstructionSegmentCount);
                    AssertBounds(afterFirstBoss.Profiles[0], -9.0, 11.0, -3.0, 7.0);

                    await CreateRectangleBoss(bridge, session, 21.0, 2.0, 20.0, 10.0, 5.0);
                    var afterSecondBoss = await session.InvokeWithApplicationAsync(app => Inspect((ModelDoc2)((SldWorks)app).ActiveDoc), CancellationToken.None);
                    TestContext?.WriteLine("After second boss: " + afterSecondBoss);
                    Assert.AreEqual(2, afterSecondBoss.Profiles.Count);
                    Assert.AreEqual(4, afterSecondBoss.Profiles[0].LineCount);
                    Assert.AreEqual(4, afterSecondBoss.Profiles[1].LineCount);
                    Assert.AreEqual(2, afterSecondBoss.Profiles[0].ConstructionSegmentCount);
                    Assert.AreEqual(2, afterSecondBoss.Profiles[1].ConstructionSegmentCount);
                    AssertBounds(afterSecondBoss.Profiles[0], -9.0, 11.0, -3.0, 7.0);
                    AssertBounds(afterSecondBoss.Profiles[1], 11.0, 31.0, -3.0, 7.0);
                    Assert.AreEqual(1, afterSecondBoss.BodyCount);
                    Assert.AreEqual(2000.0, afterSecondBoss.VolumeMm3, 0.05,
                        "Native geometry mismatch. " + afterSecondBoss);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(app =>
                    {
                        var sw = (SldWorks)app;
                        if (!string.IsNullOrWhiteSpace(createdTitle)) sw.CloseDoc(createdTitle);
                        if (original != null)
                        {
                            int errors = 0;
                            var restored = sw.ActivateDoc3(original.Title, false,
                                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
                            Assert.IsNotNull(restored);
                            Assert.AreEqual(original.Path, restored.GetPathName(), true);
                            Assert.AreEqual(original.Dirty, restored.GetSaveFlag());
                        }
                        return true;
                    }, CancellationToken.None);
                    TestContext?.WriteLine("Disposable native probe workspace: " + workspace);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run the native probe.");
#endif
        }

#if SOLIDWORKS_INTEROP
        private static async Task CreateRectangleBoss(SolidWorksBridgeFacade bridge, SolidWorksSession session,
            double x, double y, double width, double height, double depth)
        {
            await Execute(bridge, CadCommandNames.CreateSketch, new { plane = "Top Plane" });
            await Execute(bridge, CadCommandNames.AddRectangle,
                new { centerXmm = x, centerYmm = y, widthMm = width, heightMm = height });
            await Execute(bridge, CadCommandNames.ExitSketch, new { });
            await Execute(bridge, CadCommandNames.Extrude, new { depthMm = depth });
        }

        private static async Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string command, object parameters)
        {
            var result = await bridge.ExecuteAsync(new CadCommandEnvelope
            {
                Command = command, Parameters = JObject.FromObject(parameters)
            }, CancellationToken.None);
            Assert.IsNotNull(result, command + " returned no result.");
            Assert.IsTrue(result.Success, command + " failed: " + result.Error?.Message + " " + result.Error?.Detail);
            return result;
        }

        private static NativeSketchObservation Inspect(ModelDoc2 model)
        {
            var profiles = new List<ProfileBounds>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                InspectFeature(feature, profiles, visited);
            var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>();
            double? volume = null;
            if (bodies.Length == 1)
            {
                var mass = ((Body2)bodies[0]).GetMassProperties(1.0) as double[];
                if (mass != null && mass.Length > 3) volume = mass[3] * 1e9;
            }
            return new NativeSketchObservation { Profiles = profiles, BodyCount = bodies.Length, VolumeMm3 = volume ?? double.NaN };
        }

        private static void InspectFeature(Feature feature, List<ProfileBounds> profiles, HashSet<string> visited)
        {
            if (feature == null || !visited.Add(feature.Name + "|" + feature.GetTypeName2())) return;
            if (feature.GetTypeName2() == "ProfileFeature" && feature.GetSpecificFeature2() is Sketch sketch)
            {
                var segments = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>())
                    .Cast<object>().OfType<SketchSegment>().ToArray();
                var points = segments.OfType<SketchLine>()
                    .Where(line => !((SketchSegment)line).ConstructionGeometry)
                    .SelectMany(line => new[] { (SketchPoint)line.GetStartPoint2(), (SketchPoint)line.GetEndPoint2() })
                    .ToArray();
                if (points.Length > 0)
                    profiles.Add(new ProfileBounds
                    {
                        Name = feature.Name,
                        LineCount = points.Length / 2,
                        ConstructionSegmentCount = segments.Count(segment => segment.ConstructionGeometry),
                        MinXmm = points.Min(point => point.X * 1000.0),
                        MaxXmm = points.Max(point => point.X * 1000.0),
                        MinYmm = points.Min(point => point.Y * 1000.0),
                        MaxYmm = points.Max(point => point.Y * 1000.0)
                    });
            }
            for (var child = feature.GetFirstSubFeature() as Feature; child != null; child = child.GetNextSubFeature() as Feature)
                InspectFeature(child, profiles, visited);
        }

        private static void AssertBounds(ProfileBounds profile, double minX, double maxX, double minY, double maxY)
        {
            Assert.AreEqual(minX, profile.MinXmm, 1e-6, profile.Name + " minimum X");
            Assert.AreEqual(maxX, profile.MaxXmm, 1e-6, profile.Name + " maximum X");
            Assert.AreEqual(minY, profile.MinYmm, 1e-6, profile.Name + " minimum Y");
            Assert.AreEqual(maxY, profile.MaxYmm, 1e-6, profile.Name + " maximum Y");
        }

        private sealed class ProfileBounds
        {
            public string Name;
            public int LineCount, ConstructionSegmentCount;
            public double MinXmm, MaxXmm, MinYmm, MaxYmm;
            public override string ToString() => Name + " lines=" + LineCount + " construction=" + ConstructionSegmentCount + " X=[" + MinXmm.ToString("R") + "," + MaxXmm.ToString("R") +
                "] Y=[" + MinYmm.ToString("R") + "," + MaxYmm.ToString("R") + "]";
        }

        private sealed class NativeSketchObservation
        {
            public List<ProfileBounds> Profiles;
            public int BodyCount;
            public double VolumeMm3;
            public override string ToString() => "Bodies=" + BodyCount + ", VolumeMm3=" + VolumeMm3.ToString("R") + ", Profiles=" + string.Join(" | ", Profiles);
        }

        private sealed class OriginalDocument
        {
            public string Title, Path;
            public bool Dirty;
        }

        private static OriginalDocument CaptureOriginal(SldWorks sw)
        {
            var document = sw.ActiveDoc as ModelDoc2;
            return document == null ? null : new OriginalDocument
            { Title = document.GetTitle(), Path = document.GetPathName(), Dirty = document.GetSaveFlag() };
        }
#endif
    }
}
