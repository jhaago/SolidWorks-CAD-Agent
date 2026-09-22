using System;
using System.Linq;
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
        public async Task CreatePlateWithCentredThroughHole_VerifiesNativeGeometry()
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

                var bodyCount = await ExecuteRequired(bridge, CadCommandNames.GetBodyCount, new { });
                Assert.AreEqual(1, bodyCount.Data.Value<int>("bodyCount"));

                var bounds = await ExecuteRequired(bridge, CadCommandNames.GetBoundingBox, new { });
                Assert.AreEqual(100.0, bounds.Data.Value<double>("SizeXmm"), 0.02);
                Assert.AreEqual(60.0, bounds.Data.Value<double>("SizeYmm"), 0.02);
                Assert.AreEqual(10.0, bounds.Data.Value<double>("SizeZmm"), 0.02);

                var rebuild = await ExecuteRequired(bridge, CadCommandNames.GetRebuildErrors, new { });
                Assert.IsTrue(rebuild.Data.Value<bool>("Rebuilt"));
                Assert.IsFalse(rebuild.Data.Value<bool>("HasErrors"));

                var featureTree = await ExecuteRequired(bridge, CadCommandNames.GetFeatureTree, new { });
                var features = featureTree.Data["features"] as JArray;
                Assert.IsNotNull(features);
                var typeNames = features
                    .OfType<JObject>()
                    .Select(feature => feature.Value<string>("TypeName"))
                    .Where(typeName => !string.IsNullOrWhiteSpace(typeName))
                    .ToArray();

                Assert.IsTrue(typeNames.Any(typeName => typeName == "Extrusion" || typeName == "Boss"),
                    "Expected a native boss/extrusion feature.");
                Assert.IsTrue(typeNames.Any(typeName => typeName == "Cut"),
                    "Expected a native cut-extrude feature.");
            }
        }

        private static async Task<CadCommandResult> ExecuteRequired(
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
                    : string.Format("{0}: {1} ({2})", result.Error.Code, result.Error.Message, result.Error.Detail);
                Assert.Fail(commandName + " failed. " + detail);
            }

            return result;
        }
    }
}
