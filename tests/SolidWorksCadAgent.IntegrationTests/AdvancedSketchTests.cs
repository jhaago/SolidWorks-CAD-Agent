using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass]
    [TestCategory("SolidWorksIntegration")]
    public class AdvancedSketchTests
    {
        [TestMethod]
        public async Task SemicircularLineArcProfile_ExtrudesAndSurvivesSaveReopen()
        {
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                if (!status.IsConnected) status = await session.LaunchAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var relative = "integration/V2-Semicircle-" + Guid.NewGuid().ToString("N") + ".sldprt";
                string saved = null;
                try
                {
                    await Execute(bridge, "NewPart", new { });
                    await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                    await Execute(bridge, "AddLine", new { startXmm = 10.0, startYmm = 0.0, endXmm = -10.0, endYmm = 0.0 });
                    await Execute(bridge, "AddArc", new { centerXmm = 0.0, centerYmm = 0.0,
                        startXmm = -10.0, startYmm = 0.0, endXmm = 10.0, endYmm = 0.0, clockwise = true });
                    await Execute(bridge, "ExitSketch", new { });
                    await Execute(bridge, "Extrude", new { depthMm = 5.0 });
                    await Verify(bridge, session);
                    var save = await Execute(bridge, "SavePart", new { path = relative, allowOverwrite = false });
                    saved = (string)save.Data["path"];
                    Assert.IsTrue(File.Exists(saved));
                    await Execute(bridge, "CloseDocument", new { });
                    await Execute(bridge, "OpenPart", new { path = relative });
                    await Verify(bridge, session);
                }
                finally
                {
                    await bridge.ExecuteAsync(new CadCommandEnvelope { Command = "CloseDocument", Parameters = new JObject() }, CancellationToken.None);
                    if (saved != null && File.Exists(saved)) File.Delete(saved);
                }
            }
        }

        private static async Task Verify(SolidWorksBridgeFacade bridge, SolidWorksSession session)
        {
            await Execute(bridge, "Rebuild", new { });
            Assert.AreEqual(1, (int)(await Execute(bridge, "GetBodyCount", new { })).Data["bodyCount"]);
            var bounds = (await Execute(bridge, "GetBoundingBox", new { })).Data;
            var sizes = new[] { (double)bounds["sizeXmm"], (double)bounds["sizeYmm"], (double)bounds["sizeZmm"] }.OrderBy(value => value).ToArray();
            Assert.AreEqual(5.0, sizes[0], 0.02);
            Assert.AreEqual(10.0, sizes[1], 0.02);
            Assert.AreEqual(20.0, sizes[2], 0.02);
            var errors = (await Execute(bridge, "GetRebuildErrors", new { })).Data;
            Assert.IsFalse((bool)errors["hasErrors"]);
#if SOLIDWORKS_INTEROP
            var volume = await session.InvokeWithApplicationAsync(app =>
            {
                var model = (ModelDoc2)((SldWorks)app).ActiveDoc;
                var values = (double[])model.Extension.GetMassProperties2(1, out var massStatus, false);
                Assert.IsNotNull(values);
                return values[3] * 1e9;
            }, CancellationToken.None);
            Assert.AreEqual(Math.PI * 10 * 10 * 0.5 * 5, volume, 0.01, "Native volume must match a semicircle, not a full circle or open profile.");
#else
            Assert.Fail("Native interop is required for this physical acceptance test.");
#endif
        }

        private static async Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string command, object parameters)
        {
            var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = command, Parameters = JObject.FromObject(parameters) }, CancellationToken.None);
            Assert.IsTrue(result.Success, command + ": " + result.Error?.Code + " " + result.Error?.Message);
            return result;
        }
    }
}