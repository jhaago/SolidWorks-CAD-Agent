using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.References;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SolidWorksReferenceResolverTests
    {
        [TestMethod]
        public async Task SketchCapture_MissingBinding_StopsBeforeNativeCreation()
        {
            var store = new Store();
            var model = Model();
            store.Models.Add(model.ModelId, model);
            var result = await new SolidWorksReferenceResolver(store).PrepareSketchCaptureAsync(model.ModelId, Guid.NewGuid(), CancellationToken.None);
            Assert.AreEqual("SKETCH_REFERENCE_NOT_FOUND", result.Error.Code);
            Assert.IsNull(result.Binding);
        }

        [TestMethod]
        public async Task SketchCapture_WrongModelOrKind_PreservesBinding()
        {
            var store = new Store();
            var model = Model();
            store.Models.Add(model.ModelId, model);
            var binding = Binding(model.ModelId, Guid.NewGuid());
            binding.NativeReferenceBytes = new byte[] { 8, 4 };
            binding.Status = CadEntityReferenceStatus.Unresolved;
            store.Bindings.Add(binding.EntityId, binding);
            var resolver = new SolidWorksReferenceResolver(store);

            var wrongModel = await resolver.PrepareSketchCaptureAsync(Guid.NewGuid(), binding.EntityId, CancellationToken.None);
            Assert.AreEqual("MODEL_REFERENCE_NOT_FOUND", wrongModel.Error.Code);
            binding.EntityKind = "Face";
            var wrongKind = await resolver.PrepareSketchCaptureAsync(model.ModelId, binding.EntityId, CancellationToken.None);
            Assert.AreEqual("SKETCH_REFERENCE_KIND_MISMATCH", wrongKind.Error.Code);
            CollectionAssert.AreEqual(new byte[] { 8, 4 }, binding.NativeReferenceBytes);
            Assert.AreEqual(CadEntityReferenceStatus.Unresolved, binding.Status);
            Assert.AreEqual(0, store.UpdateCount);
        }

        [TestMethod]
        public async Task SketchCapture_PersistsCopiedTokenAndActivatesBinding()
        {
            var store = new Store();
            var model = Model();
            var binding = Binding(model.ModelId, Guid.NewGuid());
            store.Models.Add(model.ModelId, model);
            store.Bindings.Add(binding.EntityId, binding);
            var resolver = new SolidWorksReferenceResolver(store);
            var prepared = await resolver.PrepareSketchCaptureAsync(model.ModelId, binding.EntityId, CancellationToken.None);
            Assert.IsNull(prepared.Error);
            var token = new byte[] { 1, 2, 3 };

            var result = await resolver.PersistSketchCaptureAsync(prepared, token, CancellationToken.None);
            token[0] = 9;

            Assert.IsTrue(result.Success);
            Assert.AreEqual(binding.EntityId.ToString("D"), result.Data.Value<string>("entityId"));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, store.Bindings[binding.EntityId].NativeReferenceBytes);
            Assert.AreEqual(CadEntityReferenceStatus.Active, store.Bindings[binding.EntityId].Status);
            Assert.AreEqual(1, store.UpdateCount);
        }

        [TestMethod]
        public async Task SketchCapture_BadTokenOrStoreFailure_ReportsUncertainAndPreservesBinding()
        {
            var store = new Store();
            var model = Model();
            var binding = Binding(model.ModelId, Guid.NewGuid());
            store.Models.Add(model.ModelId, model);
            store.Bindings.Add(binding.EntityId, binding);
            var resolver = new SolidWorksReferenceResolver(store);
            var prepared = await resolver.PrepareSketchCaptureAsync(model.ModelId, binding.EntityId, CancellationToken.None);
            var malformed = await resolver.PersistSketchCaptureAsync(prepared, Array.Empty<byte>(), CancellationToken.None);
            Assert.AreEqual("SKETCH_REFERENCE_CAPTURE_UNCERTAIN", malformed.Error.Code);
            Assert.IsNull(binding.NativeReferenceBytes);
            Assert.AreEqual(CadEntityReferenceStatus.Pending, binding.Status);
            store.FailUpdate = true;
            var failedStore = await resolver.PersistSketchCaptureAsync(prepared, new byte[] { 7 }, CancellationToken.None);
            Assert.AreEqual("SKETCH_REFERENCE_PERSIST_UNCERTAIN", failedStore.Error.Code);
            Assert.IsNull(binding.NativeReferenceBytes);
            Assert.AreEqual(CadEntityReferenceStatus.Pending, binding.Status);
        }

        [TestMethod]
        public void SketchResolution_ClassifiesInvalidSuppressedDeletedAndWrongKindWithoutFallback()
        {
            Assert.AreEqual("SKETCH_REFERENCE_INVALID", SolidWorksReferenceResolver.ClassifySketchResolution(1, false).Code);
            Assert.AreEqual("SKETCH_REFERENCE_SUPPRESSED", SolidWorksReferenceResolver.ClassifySketchResolution(2, false).Code);
            Assert.AreEqual("SKETCH_REFERENCE_DELETED", SolidWorksReferenceResolver.ClassifySketchResolution(4, false).Code);
            Assert.AreEqual("SKETCH_REFERENCE_KIND_MISMATCH", SolidWorksReferenceResolver.ClassifySketchResolution(0, false).Code);
            Assert.IsNull(SolidWorksReferenceResolver.ClassifySketchResolution(0, true));
        }

        [TestMethod]
        public async Task ManagedCreateSketch_MissingBindingFailsBeforeNativeSketchCreation()
        {
            var store = new Store();
            var model = Model();
            store.Models.Add(model.ModelId, model);
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(System.IO.Path.GetTempPath()), store))
            {
                var result = await bridge.ExecuteAsync(new CadCommandEnvelope
                {
                    Command = CadCommandNames.CreateSketch,
                    Parameters = JObject.FromObject(new { plane = "Top Plane" }),
                    ExecutionId = Guid.NewGuid(), ManagedModelId = model.ModelId, OutputEntityId = Guid.NewGuid()
                }, CancellationToken.None);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("SKETCH_REFERENCE_NOT_FOUND", result.Error.Code);
                Assert.AreEqual(2, session.InvocationCount, "Identity setup and read may enter the STA, but sketch creation must not.");
            }
        }

        [TestMethod]
        public async Task SketchResolution_MalformedTokenAndWrongConfigurationDoNotEnterStaOrChangeStoredBinding()
        {
            var store = new Store();
            var model = Model();
            var binding = Binding(model.ModelId, Guid.NewGuid());
            binding.Status = CadEntityReferenceStatus.Active;
            binding.NativeReferenceBytes = Array.Empty<byte>();
            store.Models.Add(model.ModelId, model);
            store.Bindings.Add(binding.EntityId, binding);
            var session = new RecordingSession();
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(System.IO.Path.GetTempPath()), store))
            {
                var malformed = await bridge.ResolveSketchReferenceAsync(model.ModelId, binding.EntityId, CancellationToken.None);
                Assert.AreEqual("SKETCH_REFERENCE_INVALID", malformed.Error.Code);
                model.ConfigurationKey = "Other";
                var wrongConfiguration = await bridge.ResolveSketchReferenceAsync(model.ModelId, binding.EntityId, CancellationToken.None);
                Assert.AreEqual("SKETCH_REFERENCE_NOT_FOUND", wrongConfiguration.Error.Code);
                Assert.AreEqual(0, session.InvocationCount);
                Assert.AreEqual(0, store.UpdateCount);
                Assert.AreEqual(CadEntityReferenceStatus.Active, binding.Status);
                Assert.AreEqual(0, binding.NativeReferenceBytes.Length);
            }
        }

        private static CadModelIdentityRecord Model() => new CadModelIdentityRecord
        {
            ModelId = Guid.NewGuid(), ConfigurationKey = "Default", Status = CadModelIdentityStatus.ActiveUnsaved,
            CurrentModelRevisionId = Guid.NewGuid()
        };

        private static CadEntityReferenceBinding Binding(Guid modelId, Guid entityId) => new CadEntityReferenceBinding
        {
            ModelId = modelId, EntityId = entityId, ConfigurationKey = "Default", EntityKind = "Sketch",
            NativeObjectKind = "SketchFeature", ReferenceFormatVersion = 3, Status = CadEntityReferenceStatus.Pending
        };

        private sealed class Store : IModelReferenceStore
        {
            public readonly Dictionary<Guid, CadModelIdentityRecord> Models = new Dictionary<Guid, CadModelIdentityRecord>();
            public readonly Dictionary<Guid, CadEntityReferenceBinding> Bindings = new Dictionary<Guid, CadEntityReferenceBinding>();
            public int UpdateCount;
            public bool FailUpdate;
            public Task RegisterModelAsync(CadModelIdentityRecord model, CancellationToken token) => Task.CompletedTask;
            public Task<CadModelIdentityRecord> GetModelAsync(Guid id, CancellationToken token) => Task.FromResult(Models.TryGetValue(id, out var model) ? model : null);
            public Task<CadModelIdentityRecord> FindModelByCanonicalPathAsync(string path, CancellationToken token) => Task.FromResult<CadModelIdentityRecord>(null);
            public Task UpdateModelAsync(CadModelIdentityRecord model, CancellationToken token) => Task.CompletedTask;
            public Task AddEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token) => Task.CompletedTask;
            public Task<CadEntityReferenceBinding> GetEntityBindingAsync(Guid modelId, Guid entityId, string configurationKey, CancellationToken token) =>
                Task.FromResult(Bindings.TryGetValue(entityId, out var binding) && binding.ModelId == modelId && binding.ConfigurationKey == configurationKey ? binding : null);
            public Task UpdateEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token)
            {
                if (FailUpdate) throw new InvalidOperationException("store unavailable");
                Bindings[binding.EntityId] = binding;
                UpdateCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingSession : ISolidWorksSession
        {
            public int InvocationCount;
            public Task<SolidWorksSessionStatus> GetStatusAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> AttachAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<SolidWorksSessionStatus> LaunchAsync(CancellationToken token) => Task.FromResult(new SolidWorksSessionStatus());
            public Task<T> InvokeWithApplicationAsync<T>(Func<object, T> operation, CancellationToken token)
            {
                InvocationCount++;
                return Task.FromResult(operation(new object()));
            }
            public void Dispose() { }
        }
    }
}
