using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadPlanDocumentReaderTests
    {
        private const string ValidV2 = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"s1\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"s2\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"s3\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"s4\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"s5\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}" +
            "]}";

        [TestMethod]
        public void PersistedVersionTwoExtrudeBindsOnlyHostTrustedExecutionIds()
        {
            var candidate = CadPlanDocumentReader.ReadCandidate(ValidV2);
            Assert.IsTrue(candidate.IsValid);
            var normalized = CadPlanV2HostNormalizer.Normalize(candidate.CandidateV2);
            var json = JsonConvert.SerializeObject(normalized);
            var jobId = Guid.NewGuid();
            var modelId = Guid.NewGuid();

            var request = CadPlanV2ExecutionBinder.BindExtrude(json, "s5", jobId, modelId);
            Assert.AreEqual(2, request.PlanVersion);
            Assert.AreEqual(2, request.OperationVersion);
            Assert.AreEqual(jobId, request.Command.ExecutionId);
            Assert.AreEqual(modelId, request.Command.ManagedModelId);
            Assert.AreEqual(normalized.Steps[1].OutputEntityId, request.ProfileSketchEntityId);
            Assert.IsNull(request.Command.OutputEntityId);
            Assert.AreEqual(5.0, request.Command.Parameters.Value<double>("depthMm"));
            request.Command.Parameters["depthMm"] = 99;
            Assert.AreEqual(5.0, normalized.Steps[4].Parameters.Value<double>("depthMm"),
                "The execution request must not mutate the immutable reviewed plan.");
        }

        [TestMethod]
        public void PersistedVersionTwoExtrudeRejectsTamperedOrUntrustedReferences()
        {
            var normalized = CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(ValidV2).CandidateV2);
            var json = JsonConvert.SerializeObject(normalized);
            var jobId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            Assert.ThrowsException<ArgumentException>(() => CadPlanV2ExecutionBinder.BindExtrude(json, "s5", Guid.Empty, modelId));
            Assert.ThrowsException<ArgumentException>(() => CadPlanV2ExecutionBinder.BindExtrude(json, "s5", jobId, Guid.Empty));
            Assert.ThrowsException<ArgumentException>(() => CadPlanV2ExecutionBinder.BindExtrude(json, "s4", jobId, modelId));
            Assert.ThrowsException<ArgumentException>(() => CadPlanV2ExecutionBinder.BindExtrude(ValidV2, "s5", jobId, modelId),
                "A candidate without Host-normalized IDs must never become an execution request.");

            var tampered = JObject.Parse(json);
            tampered["steps"][4]["inputs"]["profileSketch"]["entityId"] = Guid.NewGuid().ToString("D");
            Assert.ThrowsException<ArgumentException>(() => CadPlanV2ExecutionBinder.BindExtrude(tampered.ToString(), "s5", jobId, modelId));
        }

        [TestMethod]
        public void UnversionedHistoricalPlanUsesLegacyReaderWithoutChangingSelectionSemantics()
        {
            const string json = "{\"Summary\":\"legacy\",\"ProposedCommands\":[{\"Command\":\"Extrude\",\"Parameters\":{\"depthMm\":5}}]}";
            var result = CadPlanDocumentReader.Read(json);
            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(1, result.Version);
            Assert.IsNotNull(result.LegacyPlan);
            Assert.IsNull(result.CandidateV2);
            Assert.IsNull(result.LegacyPlan.ProposedCommands.Single().Parameters["sketchEntityId"]);
        }

        [TestMethod]
        public void ValidVersionTwoCandidateIsParsedAndPreflightedButNotLoweredToLegacy()
        {
            var result = CadPlanDocumentReader.Read(ValidV2);
            Assert.IsTrue(result.IsValid, string.Join(" ", result.Errors));
            Assert.AreEqual(2, result.Version);
            Assert.IsNull(result.LegacyPlan);
            Assert.IsNotNull(result.CandidateV2);
            Assert.AreEqual("profile", result.CandidateV2.Steps.Last().Inputs.ProfileSketch.OutputKey);
            Assert.AreEqual(2, result.CandidateV2.Steps.Last().OperationVersion);
        }

        [DataTestMethod]
        [DataRow("{\"planVersion\":3,\"steps\":[]}", "UNSUPPORTED_PLAN_VERSION")]
        [DataRow("{\"planVersion\":999999999999999999999999,\"steps\":[]}", "PLAN_VERSION_INVALID")]
        [DataRow("{\"steps\":[]}", "PLAN_VERSION_REQUIRED")]
        [DataRow("{\"PlanVersion\":2,\"steps\":[]}", "PLAN_VERSION_INVALID")]
        [DataRow("{\"planVersion\":2,\"steps\":[{\"stepKey\":\"x\",\"command\":\"NewPart\",\"parameters\":{}}]}", "INVALID_PLAN_DOCUMENT")]
        public void InvalidOrUnversionedV2DocumentsFailClosed(string json, string expected)
        {
            var result = CadPlanDocumentReader.Read(json);
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith(expected)), string.Join(" ", result.Errors));
            Assert.IsNull(result.LegacyPlan);
        }

        [DataTestMethod]
        [DataRow("operationVersion", "3", "Unsupported operationVersion")]
        [DataRow("stepKey", "\"s1\"", "Duplicate stepKey")]
        [DataRow("outputKey", "\"profile\"", "Duplicate outputKey")]
        [DataRow("outputKey", "\"missing\"", "Dangling profile sketch")]
        [DataRow("kind", "\"Face\"", "kind must be Sketch")]
        public void CandidateReferenceAndVersionErrorsAreRejected(string field, string value, string expected)
        {
            var json = ValidV2;
            if (field == "operationVersion")
                json = json.Replace("\"operationVersion\":2", "\"operationVersion\":" + value);
            else if (field == "stepKey")
                json = json.Replace("\"stepKey\":\"s5\"", "\"stepKey\":" + value);
            else if (field == "outputKey" && value == "\"profile\"")
                json = json.Replace("\"stepKey\":\"s4\",\"command\":\"ExitSketch\"", "\"stepKey\":\"s4\",\"command\":\"CreateSketch\"")
                    .Replace("\"stepKey\":\"s4\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{}", "\"stepKey\":\"s4\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"");
            else if (field == "outputKey")
                json = json.Replace("\"outputKey\":\"profile\"}}}", "\"outputKey\":\"missing\"}}}");
            else
                json = json.Replace("\"kind\":\"Sketch\"", "\"kind\":" + value);

            var result = CadPlanDocumentReader.Read(json);
            Assert.IsFalse(result.IsValid, string.Join(" ", result.Errors));
            Assert.IsTrue(result.Errors.Any(error => error.IndexOf(expected, System.StringComparison.OrdinalIgnoreCase) >= 0),
                string.Join(" ", result.Errors));
        }

        [TestMethod]
        public void DuplicateJsonPropertyNamesAreRejectedBeforeDeserialization()
        {
            var result = CadPlanDocumentReader.Read("{\"planVersion\":2,\"planVersion\":2,\"steps\":[]}");
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(error => error.StartsWith("INVALID_PLAN_DOCUMENT")));
        }

        [TestMethod]
        public void VersionTwoCaseVariantSchemaAliasesAreRejectedBeforeDtoBinding()
        {
            var aliases = new[]
            {
                ValidV2.Replace("\"planVersion\":2", "\"planVersion\":2,\"PlanVersion\":2"),
                ValidV2.Replace("\"steps\":", "\"Steps\":"),
                ValidV2.Replace("\"profileSketch\":", "\"ProfileSketch\":"),
                ValidV2.Replace("\"depthMm\":5", "\"depthMm\":5,\"DepthMm\":99")
            };
            foreach (var json in aliases)
            {
                var result = CadPlanDocumentReader.ReadCandidate(json);
                Assert.IsFalse(result.IsValid, "A case-variant alias must not change a reviewed v2 plan: " + json);
                Assert.IsTrue(result.Errors.Any(error => error.StartsWith("INVALID_PLAN_DOCUMENT", StringComparison.Ordinal)),
                    string.Join(" ", result.Errors));
            }
        }

        [TestMethod]
        public void VersionTwoPreflightUsesNamedSketchReferencesAcrossMultipleFeatures()
        {
            const string twoProfiles = "{\"planVersion\":2,\"steps\":[" +
                "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
                "{\"stepKey\":\"a\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile-a\"}," +
                "{\"stepKey\":\"ra\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
                "{\"stepKey\":\"ea\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
                "{\"stepKey\":\"b\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile-b\"}," +
                "{\"stepKey\":\"rb\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":30,\"centerYmm\":0,\"widthMm\":10,\"heightMm\":10}}," +
                "{\"stepKey\":\"eb\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
                "{\"stepKey\":\"boss-a\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile-a\"}}}," +
                "{\"stepKey\":\"boss-b\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile-b\"}}}]}";
            var candidate = CadPlanDocumentReader.ReadCandidate(twoProfiles);
            Assert.IsTrue(candidate.IsValid, string.Join(" ", candidate.Errors));
            var normalized = CadPlanV2HostNormalizer.Normalize(candidate.CandidateV2);
            Assert.IsTrue(CadPlanDocumentReader.ReadPersisted(JsonConvert.SerializeObject(normalized)).IsValid);

            var repeated = JObject.Parse(twoProfiles);
            repeated["steps"][8]["inputs"]["profileSketch"]["outputKey"] = "profile-a";
            var duplicateConsumer = CadPlanDocumentReader.ReadCandidate(repeated.ToString());
            Assert.IsFalse(duplicateConsumer.IsValid);
            Assert.IsTrue(duplicateConsumer.Errors.Any(error => error.Contains("only one feature")),
                string.Join(" ", duplicateConsumer.Errors));
        }

        [TestMethod]
        public void UnsupportedRootAndStepPropertiesDoNotGetSilentlyIgnored()
        {
            var root = ValidV2.Replace("\"planVersion\":2", "\"planVersion\":2,\"nativeToken\":\"secret\"");
            var rootResult = CadPlanDocumentReader.Read(root);
            Assert.IsTrue(rootResult.Errors.Any(error => error.Contains("Unexpected version-2 root property")));

            var step = ValidV2.Replace("\"stepKey\":\"s1\"", "\"stepKey\":\"s1\",\"faceIndex\":4");
            var stepResult = CadPlanDocumentReader.Read(step);
            Assert.IsTrue(stepResult.Errors.Any(error => error.Contains("Unexpected property 'faceIndex'")));
        }

        [TestMethod]
        public void ForwardSketchReferencesAreRejectedEvenWhenProducerExistsLater()
        {
            var json = ValidV2.Replace("\"outputKey\":\"profile\"}}}", "\"outputKey\":\"future\"}}}")
                .Replace("]}", ",{\"stepKey\":\"s6\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"future\"},{\"stepKey\":\"s7\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}]}");

            var result = CadPlanDocumentReader.Read(json);
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("must reference a sketch created by an earlier step")), string.Join(" ", result.Errors));
        }

        [TestMethod]
        public void CandidateCannotReferenceAnOpenedDocument()
        {
            var json = ValidV2.Replace("\"command\":\"NewPart\"", "\"command\":\"OpenPart\"");
            var result = CadPlanDocumentReader.Read(json);
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(error => error.Contains("OpenPart references are unsupported")), string.Join(" ", result.Errors));
        }

        [TestMethod]
        public void CandidateCannotChooseHostEntityIdsAndPersistedReferencesMustMatchProducer()
        {
            var suppliedId = ValidV2.Replace("\"outputKey\":\"profile\"", "\"outputKey\":\"profile\",\"outputEntityId\":\"11111111-1111-4111-8111-111111111111\"");
            var candidateResult = CadPlanDocumentReader.ReadCandidate(suppliedId);
            Assert.IsFalse(candidateResult.IsValid);
            Assert.IsTrue(candidateResult.Errors.Any(error => error.Contains("cannot assign Host outputEntityId")), string.Join(" ", candidateResult.Errors));

            var validCandidate = CadPlanDocumentReader.ReadCandidate(ValidV2);
            var normalized = CadPlanV2HostNormalizer.Normalize(validCandidate.CandidateV2);
            normalized.Steps.Last().Inputs.ProfileSketch.EntityId = Guid.NewGuid();
            var persistedResult = CadPlanDocumentReader.ReadPersisted(JsonConvert.SerializeObject(normalized));
            Assert.IsFalse(persistedResult.IsValid);
            Assert.IsTrue(persistedResult.Errors.Any(error => error.Contains("does not match the Host ID assigned")), string.Join(" ", persistedResult.Errors));
        }
    }
}
