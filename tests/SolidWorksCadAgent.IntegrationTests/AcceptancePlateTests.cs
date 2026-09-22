using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass]
    [TestCategory("SolidWorksIntegration")]
    public class AcceptancePlateTests
    {
        [TestMethod]
        public async Task CreatePlateWithCentredThroughHole_LeavesNativePartActive()
        {
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                if (!status.IsConnected)
                {
                    status = await session.LaunchAsync(CancellationToken.None);
                }

                Assert.IsTrue(status.IsConnected, status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);

                await ExecuteRequired(bridge, CadCommandNames.NewPart, new { });
                await ExecuteRequired(bridge, CadCommandNames.CreateSketch, new { plane = "Top Plane" });
                await ExecuteRequired(bridge, CadCommandNames.AddRectangle, new
                {
                    centerXmm = 0.0,
                    centerYmm = 0.0,
                    widthMm = 100.0,
                    heightMm = 60.0
                });
                await ExecuteRequired(bridge, CadCommandNames.ExitSketch, new { });
                await ExecuteRequired(bridge, CadCommandNames.Extrude, new { depthMm = 10.0 });
                await ExecuteRequired(bridge, CadCommandNames.CreateSketch, new { plane = "Top Plane" });
                await ExecuteRequired(bridge, CadCommandNames.AddCircle, new
                {
                    centerXmm = 0.0,
                    centerYmm = 0.0,
                    diameterMm = 20.0
                });
                await ExecuteRequired(bridge, CadCommandNames.ExitSketch, new { });
                await ExecuteRequired(bridge, CadCommandNames.CutExtrude, new { endCondition = "ThroughAll" });
                await ExecuteRequired(bridge, CadCommandNames.Rebuild, new { });

                var hasActivePart = await session.InvokeWithApplicationAsync(application =>
                {
                    dynamic swApp = application;
                    dynamic model = swApp.ActiveDoc;
                    return model != null;
                }, CancellationToken.None);

                Assert.IsTrue(hasActivePart, "One native SOLIDWORKS part document should remain active.");
            }
        }

        private static async Task ExecuteRequired(
            SolidWorksBridgeFacade bridge,
            string commandName,
            object parameters)
        {
            var result = await bridge.ExecuteAsync(
                new CadCommandEnvelope
                {
                    Command = commandName,
                    Parameters = parameters == null ? new JObject() : JObject.FromObject(parameters)
                },
                CancellationToken.None);

            if (!result.Success)
            {
                var detail = result.Error == null
                    ? "Unknown CAD command failure."
                    : string.Format("{0}: {1}", result.Error.Code, result.Error.Message);
                Assert.Fail(commandName + " failed. " + detail);
            }
        }
    }
}
