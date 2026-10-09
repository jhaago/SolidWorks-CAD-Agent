using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Workspace;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadV2ArtifactAuthorizationTests
    {
        [TestMethod]
        public void DeliveryRequiresCurrentApprovedSaveAttemptAndLatestMatchingVerification()
        {
            var root = Path.Combine(Path.GetTempPath(), "cad-v2-artifact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "owned.sldprt");
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                var candidate = JObject.Parse("{\"planVersion\":2,\"steps\":[" +
                    "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
                    "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
                    "{\"stepKey\":\"rect\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
                    "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
                    "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}]} ");
                ((JArray)candidate["steps"]).Add(new JObject
                {
                    ["stepKey"] = "save", ["command"] = "SavePart", ["operationVersion"] = 1,
                    ["parameters"] = new JObject { ["path"] = path, ["allowOverwrite"] = false }
                });
                var parsed = CadPlanDocumentReader.ReadCandidate(candidate.ToString(Formatting.None));
                Assert.IsTrue(parsed.IsValid, string.Join(" ", parsed.Errors));
                var json = JsonConvert.SerializeObject(CadPlanV2HostNormalizer.Normalize(parsed.CandidateV2));
                var persisted = CadPlanDocumentReader.ReadPersisted(json);
                Assert.IsTrue(persisted.IsValid, string.Join(" ", persisted.Errors));
                var planSha = Hash(Encoding.UTF8.GetBytes(json));
                var jobId = Guid.NewGuid();
                var revision = new JobRevision { Id = Guid.NewGuid(), JobId = jobId, RevisionNumber = 1, PlanJson = json };
                var model = new CadModelIdentityRecord
                {
                    ModelId = Guid.NewGuid(), CurrentModelRevisionId = Guid.NewGuid(), CanonicalPath = path,
                    LastSavedSha256 = new string('a', 64), Status = CadModelIdentityStatus.ActiveSaved,
                    DocumentKind = "Part"
                };
                var attempt = (CadV2MutationAttemptRecord)Activator.CreateInstance(typeof(CadV2MutationAttemptRecord),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { Guid.NewGuid(), jobId, revision.Id, planSha, "save", model.ModelId,
                        null, model.CurrentModelRevisionId, CadV2MutationAttemptStatus.Applied }, null);
                var save = new CommandExecutionRecord { JobId = jobId, RevisionNumber = 1,
                    CommandName = "SavePart", Success = true, ResultJson = new JObject { ["path"] = path }.ToString() };
                var pass = new VerificationResultRecord { JobId = jobId, RevisionNumber = 1,
                    CheckName = CadV2SavedVerificationRecorder.CheckName, Passed = true,
                    ExpectedJson = new JObject { ["planSha256"] = planSha, ["modelId"] = model.ModelId.ToString("D"),
                        ["modelRevisionId"] = model.CurrentModelRevisionId.ToString("D"),
                        ["path"] = path, ["sha256"] = model.LastSavedSha256 }.ToString(),
                    ActualJson = new JObject { ["sha256"] = model.LastSavedSha256 }.ToString() };
                var snapshot = new JobSnapshot { Job = new CadJob { Id = jobId, State = JobState.ReadyForReview,
                    PlanValidated = true },
                    Revisions = new[] { revision }, Commands = new[] { save }, Verifications = new[] { pass } };
                var workspace = new WorkspacePolicy(root);
                Assert.IsTrue(CadV2ArtifactAuthorization.MatchesBytes(new byte[] { 1, 2, 3 },
                    Hash(new byte[] { 1, 2, 3 })));
                Assert.IsFalse(CadV2ArtifactAuthorization.MatchesBytes(new byte[] { 1, 2, 4 },
                    Hash(new byte[] { 1, 2, 3 })));
                Assert.IsTrue(CadV2ArtifactAuthorization.IsAuthorized(snapshot, revision, save, attempt,
                    model, jobId, path, workspace));
                Assert.IsFalse(CadV2ArtifactAuthorization.IsAuthorized(snapshot, revision, save, attempt,
                    model, Guid.NewGuid(), path, workspace));
                snapshot.Verifications = new[] { pass, new VerificationResultRecord { JobId = jobId,
                    RevisionNumber = 1, CheckName = pass.CheckName, Passed = false } };
                Assert.IsFalse(CadV2ArtifactAuthorization.IsAuthorized(snapshot, revision, save, attempt,
                    model, jobId, path, workspace));
                snapshot.Verifications = new[] { pass };
                model.LastSavedSha256 = new string('b', 64);
                Assert.IsFalse(CadV2ArtifactAuthorization.IsAuthorized(snapshot, revision, save, attempt,
                    model, jobId, path, workspace));
            }
            finally { Directory.Delete(root, true); }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
