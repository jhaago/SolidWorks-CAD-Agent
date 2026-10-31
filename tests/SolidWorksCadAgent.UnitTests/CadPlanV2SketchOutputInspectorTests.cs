using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadPlanV2SketchOutputInspectorTests
    {
        private const string Candidate = "{\"planVersion\":2,\"steps\":[" +
            "{\"stepKey\":\"new\",\"command\":\"NewPart\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"sketch\",\"command\":\"CreateSketch\",\"operationVersion\":1,\"parameters\":{\"plane\":\"Top Plane\"},\"outputKey\":\"profile\"}," +
            "{\"stepKey\":\"rectangle\",\"command\":\"AddRectangle\",\"operationVersion\":1,\"parameters\":{\"centerXmm\":0,\"centerYmm\":0,\"widthMm\":20,\"heightMm\":10}}," +
            "{\"stepKey\":\"exit\",\"command\":\"ExitSketch\",\"operationVersion\":1,\"parameters\":{}}," +
            "{\"stepKey\":\"boss\",\"command\":\"Extrude\",\"operationVersion\":2,\"parameters\":{\"depthMm\":5},\"inputs\":{\"profileSketch\":{\"kind\":\"Sketch\",\"outputKey\":\"profile\"}}}]}";

        [TestMethod]
        public async Task InspectorReportsPendingCapturedAndUncertainStatesWithoutClaimingCommit()
        {
            await WithRepositoryAsync(async repository =>
            {
                var model = await RegisterModelAsync(repository);
                var json = NormalizedPlan();
                var sketchId = CadPlanDocumentReader.ReadPersisted(json).CandidateV2.Steps[1].OutputEntityId.Value;
                var sketchRevision = Guid.NewGuid();
                var binding = NewPendingBinding(model, sketchId, sketchRevision);
                await repository.AddEntityBindingAsync(binding, CancellationToken.None);

                var pending = await CadPlanV2SketchOutputInspector.ObserveExtrudeInputAsync(
                    json, "boss", model.ModelId, repository, CancellationToken.None);
                Assert.AreEqual(sketchId, pending.EntityId);
                Assert.AreEqual(CadEntityReferenceStatus.Pending, pending.ReferenceStatus);
                Assert.IsFalse(pending.HasStoredToken);
                Assert.AreEqual(model.CurrentModelRevisionId, pending.ModelRevisionId);
                Assert.AreEqual(sketchRevision, pending.BindingCreatedAtRevisionId);

                binding.Status = CadEntityReferenceStatus.Active;
                binding.NativeReferenceBytes = new byte[] { 1, 2, 3 };
                await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
                var capturedButUncommitted = await CadPlanV2SketchOutputInspector.ObserveExtrudeInputAsync(
                    json, "boss", model.ModelId, repository, CancellationToken.None);
                Assert.AreEqual(CadEntityReferenceStatus.Active, capturedButUncommitted.ReferenceStatus);
                Assert.IsTrue(capturedButUncommitted.HasStoredToken);
                Assert.AreEqual(model.CurrentModelRevisionId, capturedButUncommitted.ModelRevisionId,
                    "A captured token must not be mistaken for a committed model revision.");
                Assert.AreNotEqual(sketchRevision, capturedButUncommitted.ModelRevisionId);

                model.Status = CadModelIdentityStatus.Uncertain;
                await repository.UpdateModelAsync(model, CancellationToken.None);
                binding.Status = CadEntityReferenceStatus.Uncertain;
                await repository.UpdateEntityBindingAsync(binding, CancellationToken.None);
                var uncertain = await CadPlanV2SketchOutputInspector.ObserveExtrudeInputAsync(
                    json, "boss", model.ModelId, repository, CancellationToken.None);
                Assert.AreEqual(CadModelIdentityStatus.Uncertain, uncertain.ModelStatus);
                Assert.AreEqual(CadEntityReferenceStatus.Uncertain, uncertain.ReferenceStatus);
            });
        }

        [TestMethod]
        public async Task InspectorDoesNotBorrowBindingFromAnotherConfigurationOrUnnormalizedPlan()
        {
            await WithRepositoryAsync(async repository =>
            {
                var model = await RegisterModelAsync(repository);
                var json = NormalizedPlan();
                var sketchId = CadPlanDocumentReader.ReadPersisted(json).CandidateV2.Steps[1].OutputEntityId.Value;
                await repository.AddEntityBindingAsync(NewPendingBinding(model, sketchId, Guid.NewGuid()), CancellationToken.None);
                model.ConfigurationKey = "Other";
                await repository.UpdateModelAsync(model, CancellationToken.None);

                var observation = await CadPlanV2SketchOutputInspector.ObserveExtrudeInputAsync(
                    json, "boss", model.ModelId, repository, CancellationToken.None);
                Assert.IsNull(observation.ReferenceStatus);
                Assert.IsFalse(observation.HasStoredToken);
                await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                    CadPlanV2SketchOutputInspector.ObserveExtrudeInputAsync(
                        Candidate, "boss", model.ModelId, repository, CancellationToken.None));
            });
        }

        private static string NormalizedPlan() => JsonConvert.SerializeObject(
            CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(Candidate).CandidateV2));

        private static async Task<CadModelIdentityRecord> RegisterModelAsync(SqliteJobRepository repository)
        {
            var now = DateTime.UtcNow;
            var model = new CadModelIdentityRecord
            {
                ModelId = Guid.NewGuid(), DocumentKind = "Part", Status = CadModelIdentityStatus.ActiveUnsaved,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId", CurrentModelRevisionId = Guid.NewGuid(),
                ConfigurationKey = "Default", RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now
            };
            await repository.RegisterModelAsync(model, CancellationToken.None);
            return model;
        }

        private static CadEntityReferenceBinding NewPendingBinding(CadModelIdentityRecord model, Guid entityId, Guid revisionId)
        {
            var now = DateTime.UtcNow;
            return new CadEntityReferenceBinding
            {
                ModelId = model.ModelId, EntityId = entityId, EntityKind = "Sketch",
                ConfigurationKey = model.ConfigurationKey, NativeObjectKind = "SketchFeature",
                ReferenceFormatVersion = 3, CreatedAtModelRevisionId = revisionId,
                Status = CadEntityReferenceStatus.Pending, CreatedUtc = now, UpdatedUtc = now
            };
        }

        private static async Task WithRepositoryAsync(Func<SqliteJobRepository, Task> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "cad-v2-inspect-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                using (var repository = new SqliteJobRepository(path))
                {
                    await repository.InitializeAsync();
                    await action(repository);
                }
            }
            finally
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
        }
    }
}
