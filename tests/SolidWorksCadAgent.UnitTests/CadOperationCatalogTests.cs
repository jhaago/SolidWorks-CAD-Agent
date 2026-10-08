using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadOperationCatalogTests
    {
        [TestMethod]
        public void CatalogIsUniqueVersionOneAndDrivesPlannerAllowlist()
        {
            var descriptors = CadOperationCatalog.Descriptors;
            var expectedNames = new[]
            {
                CadCommandNames.NewPart, CadCommandNames.OpenPart, CadCommandNames.SavePart,
                CadCommandNames.CloseDocument, CadCommandNames.CreateSketch, CadCommandNames.AddLine,
                CadCommandNames.AddArc, CadCommandNames.AddRectangle, CadCommandNames.AddCircle,
                CadCommandNames.AddSlot, CadCommandNames.AddRegularPolygon, CadCommandNames.ExitSketch,
                CadCommandNames.Extrude, CadCommandNames.CutExtrude, CadCommandNames.Rebuild
            };
            Assert.IsTrue(descriptors.Count > 0);
            Assert.AreEqual(descriptors.Count, descriptors.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.IsTrue(descriptors.All(d => d.OperationVersion == 1));
            CollectionAssert.AreEqual(expectedNames, descriptors.Select(d => d.Name).ToArray());
            CollectionAssert.AreEqual(descriptors.Select(d => d.Name).ToArray(), CadPlanningCommandContract.AllowedCommands.ToArray());
        }

        [TestMethod]
        public void PlannerProtocolTextIsGeneratedFromOperationDescriptors()
        {
            foreach (var descriptor in CadOperationCatalog.Descriptors)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.ProtocolText), descriptor.Name);
                StringAssert.Contains(CadPlanningCommandContract.ProtocolDescription, descriptor.ProtocolText);
            }
        }

        [TestMethod]
        public void EveryPlannerOperationHasAnExecutorHandlerAndWipCommandsStayUnavailable()
        {
            using (var bridge = new SolidWorksBridgeFacade(new NeverInvokeSession()))
            {
                var registered = new HashSet<string>(bridge.RegisteredCommandNames, StringComparer.Ordinal);
                foreach (var descriptor in CadOperationCatalog.Descriptors)
                    Assert.IsTrue(registered.Contains(descriptor.Name), descriptor.Name + " is advertised but not registered.");
            }

            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("CreateSketchOnFace"));
            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("FilletEdges"));
            Assert.IsFalse(CadPlanningCommandContract.AllowedCommands.Contains("ChamferEdges"));
        }

        [TestMethod]
        public void ExistingCommandEnvelopeShapeRemainsReadableWithoutVersionField()
        {
            const string legacyJson = "{\"command\":\"CreateSketch\",\"parameters\":{\"plane\":\"Top Plane\"}}";
            var command = JsonConvert.DeserializeObject<CadCommandEnvelope>(legacyJson);

            Assert.AreEqual(CadCommandNames.CreateSketch, command.Command);
            Assert.AreEqual("Top Plane", (string)command.Parameters["plane"]);
            Assert.IsNull(CadPlanningCommandContract.Validate(command));
            StringAssert.Contains(JsonConvert.SerializeObject(command), "\"Command\"");
            Assert.IsFalse(JsonConvert.SerializeObject(command).Contains("OperationVersion"));
        }

        [TestMethod]
        public void HistoricalFeatureCommandsRemainVersionOneAndRejectReferenceParameters()
        {
            foreach (var json in new[]
            {
                "{\"command\":\"Extrude\",\"parameters\":{\"depthMm\":10}}",
                "{\"command\":\"CutExtrude\",\"parameters\":{\"endCondition\":\"ThroughAll\"}}"
            })
            {
                var historical = JsonConvert.DeserializeObject<CadCommandEnvelope>(json);
                Assert.IsNull(CadPlanningCommandContract.Validate(historical));
                Assert.AreEqual(1, CadOperationCatalog.Find(historical.Command).OperationVersion);
                Assert.IsFalse(JsonConvert.SerializeObject(historical).Contains("OperationVersion"));
                historical.Parameters["sketchEntityId"] = Guid.NewGuid().ToString("D");
                Assert.IsNotNull(CadPlanningCommandContract.Validate(historical),
                    "An unversioned feature cannot silently acquire a logical sketch reference.");
            }
        }

        [TestMethod]
        public void EveryCatalogOperationReachesItsExistingParameterValidator()
        {
            foreach (var descriptor in CadOperationCatalog.Descriptors)
            {
                var error = CadPlanningCommandContract.Validate(new CadCommandEnvelope
                {
                    Command = descriptor.Name,
                    Parameters = new JObject()
                });

                Assert.IsTrue(error == null || !error.StartsWith("The proposed CAD plan contains an unsupported command", StringComparison.Ordinal),
                    descriptor.Name + " is catalogued but has no planner validation case.");
            }
        }

        private sealed class NeverInvokeSession : ISolidWorksSession
        {
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => GetStatusAsync(token);
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken token) => throw new InvalidOperationException("Registry inspection must not invoke COM.");
            public void Dispose() { }
        }
    }
}
