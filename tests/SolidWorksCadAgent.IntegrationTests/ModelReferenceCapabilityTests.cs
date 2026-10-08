using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
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
    [TestCategory("PersistentReference")]
    public class ModelReferenceCapabilityTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        [Timeout(180000)]
        public async Task PersistentSketchReference_ResolvesAfterSaveReopenRenameAndRebuild()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", Guid.NewGuid().ToString("N"));
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace)))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, "Start SOLIDWORKS before opting into the reference test. " + status.ErrorMessage);
                Assert.IsNotNull(status.RuntimeInfo);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear, "This probe targets installed SOLIDWORKS 2020.");

                var original = await session.InvokeWithApplicationAsync(application => CaptureOriginal((SldWorks)application), CancellationToken.None);
                var ownedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string savedPath = null;
                try
                {
                    Directory.CreateDirectory(workspace);
                    var created = await Execute(bridge, "NewPart", new { });
                    var title = created.Data.Value<string>("documentTitle");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(title));
                    Assert.AreNotEqual(original == null ? null : original.Title, title, "NewPart must create a distinct owned document.");
                    ownedTitles.Add(title);
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        var getResult = model.Extension.CustomPropertyManager[""].Get6("SolidWorksCadAgent.ModelId", false,
                            out _, out _, out _, out _);
                        Assert.AreEqual((int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent, getResult,
                            "Legacy NewPart calls without Host identity metadata must not stamp an Agent model ID.");
                        return true;
                    }, CancellationToken.None);

                    await Execute(bridge, "CreateSketch", new { plane = "Top Plane" });
                    await Execute(bridge, "AddRectangle", new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 });
                    var persistentReference = await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        var sketch = model.SketchManager.ActiveSketch;
                        Assert.IsNotNull(sketch, "Capture the reference while the newly created sketch is active.");
                        var bytes = model.Extension.GetPersistReference3(sketch) as byte[];
                        Assert.IsNotNull(bytes, "GetPersistReference3 must return a byte-array reference for the sketch.");
                        Assert.IsTrue(bytes.Length > 0, "The native reference byte-array must not be empty.");
                        TestContext?.WriteLine("Captured native sketch reference bytes: " + bytes.Length);
                        return bytes.ToArray();
                    }, CancellationToken.None);
                    await Execute(bridge, "ExitSketch", new { });

                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        AssertSketchReferenceResolves(model, persistentReference, "before save");
                        return true;
                    }, CancellationToken.None);

                    var save = await Execute(bridge, "SavePart", new { path = "persistent-sketch-reference.sldprt", allowOverwrite = false });
                    savedPath = save.Data.Value<string>("path");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(savedPath));
                    Assert.AreEqual(Path.GetFullPath(Path.Combine(workspace, "persistent-sketch-reference.sldprt")), Path.GetFullPath(savedPath), true);
                    Assert.IsTrue(File.Exists(savedPath));
                    TestContext?.WriteLine("Native reference probe artifact retained after owned-document cleanup: " + savedPath);
                    ownedTitles.Add(await session.InvokeWithApplicationAsync(application => ((ModelDoc2)((SldWorks)application).ActiveDoc).GetTitle(), CancellationToken.None));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    await Execute(bridge, "CloseDocument", new { });
                    var reopened = await Execute(bridge, "OpenPart", new { path = "persistent-sketch-reference.sldprt" });
                    ownedTitles.Add(reopened.Data.Value<string>("documentTitle"));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);

                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        AssertSketchReferenceResolves(model, persistentReference, "after close and reopen");
                        var sketchFeature = FindSketchFeature(model);
                        Assert.IsNotNull(sketchFeature, "The owned model must contain the created sketch feature.");
                        sketchFeature.Name = "PersistentReferenceRenameProbe";
                        Assert.IsTrue(model.EditRebuild3(), "The renamed owned model must rebuild successfully.");
                        AssertSketchReferenceResolves(model, persistentReference, "after sketch rename and rebuild");
                        return true;
                    }, CancellationToken.None);

                    TestContext?.WriteLine("Native SOLIDWORKS release: " + status.RuntimeInfo.ReleaseYear + "; retained owned artifact: " + savedPath);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var sw = (SldWorks)application;
                        try
                        {
                            foreach (var document in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                            {
                                var title = document.GetTitle();
                                var path = document.GetPathName();
                                var isOwned = string.IsNullOrEmpty(path) ? ownedTitles.Contains(title) : IsInside(path, workspace);
                                if (!isOwned) continue;
                                Assert.IsFalse(IsOriginal(document, original), "Cleanup must never close the user's original document.");
                                sw.CloseDoc(title);
                            }
                            Assert.IsFalse((sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>().Any(document =>
                                string.IsNullOrEmpty(document.GetPathName()) ? ownedTitles.Contains(document.GetTitle()) : IsInside(document.GetPathName(), workspace)),
                                "All owned test documents must be closed.");
                        }
                        finally
                        {
                            RestoreAndAssertOriginal(sw, original);
                        }
                        return true;
                    }, CancellationToken.None);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run native reference acceptance.");
#endif
        }

        [TestMethod]
        [Timeout(180000)]
        public async Task ManagedPartIdentity_IsStampedAndValidatedAfterSaveAndReopen()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", Guid.NewGuid().ToString("N"));
            var store = new InMemoryModelReferenceStore();
            var modelId = Guid.NewGuid();
            store.RegisterModelAsync(new CadModelIdentityRecord
            {
                ModelId = modelId,
                DocumentKind = "Part",
                Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId",
                ConfigurationKey = "Pending",
                CurrentModelRevisionId = Guid.NewGuid(),
                RegistryVersion = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            }, CancellationToken.None).GetAwaiter().GetResult();

            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace), store))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, "Start SOLIDWORKS before opting into the managed identity test. " + status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var original = await session.InvokeWithApplicationAsync(application => CaptureOriginal((SldWorks)application), CancellationToken.None);
                var ownedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string savedPath = null;
                var executionId = Guid.NewGuid();
                try
                {
                    Directory.CreateDirectory(workspace);
                    var created = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.NewPart,
                        Parameters = new JObject(),
                        ExecutionId = executionId,
                        ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(created.Success, created.Error?.Code + " " + created.Error?.Message);
                    var title = created.Data.Value<string>("documentTitle");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(title));
                    ownedTitles.Add(title);
                    Assert.AreEqual("Default", created.Data.Value<string>("configurationKey"));
                    Assert.AreEqual(modelId.ToString("D"), created.Data.Value<string>("modelId"),
                        "NewPart must return the Host identity only after verifying the native custom property.");

                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        string value;
                        model.Extension.CustomPropertyManager[""].Get6("SolidWorksCadAgent.ModelId", false,
                            out value, out _, out _, out _);
                        Assert.AreEqual(modelId.ToString("D"), value, "NewPart must stamp and read back the exact Host ID.");
                        return true;
                    }, CancellationToken.None);

                    var save = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.SavePart,
                        Parameters = JObject.FromObject(new { path = "managed-identity-reopen.sldprt", allowOverwrite = false }),
                        ExecutionId = executionId
                    }, CancellationToken.None);
                    Assert.IsTrue(save.Success, save.Error?.Code + " " + save.Error?.Message);
                    savedPath = save.Data.Value<string>("path");
                    Assert.IsTrue(File.Exists(savedPath));
                    var record = await store.GetModelAsync(modelId, CancellationToken.None);
                    record.Status = CadModelIdentityStatus.ActiveSaved;
                    record.ConfigurationKey = "Default";
                    record.CanonicalPath = Path.GetFullPath(savedPath);
                    using (var stream = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        record.LastSavedSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                    await store.UpdateModelAsync(record, CancellationToken.None);
                    TestContext?.WriteLine("Managed model ID: " + modelId.ToString("D") + "; canonical path: " + savedPath + "; SHA-256: " + record.LastSavedSha256);

                    var close = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = CadCommandNames.CloseDocument, Parameters = new JObject(), ExecutionId = executionId }, CancellationToken.None);
                    Assert.IsTrue(close.Success, close.Error?.Code + " " + close.Error?.Message);
                    var opened = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.OpenPart,
                        Parameters = JObject.FromObject(new { path = "managed-identity-reopen.sldprt" }),
                        ExecutionId = executionId
                    }, CancellationToken.None);
                    Assert.IsTrue(opened.Success, opened.Error?.Code + " " + opened.Error?.Message);
                    ownedTitles.Add(opened.Data.Value<string>("documentTitle"));
                    var inspect = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = CadCommandNames.GetFeatureTree, Parameters = new JObject(), ExecutionId = executionId }, CancellationToken.None);
                    Assert.IsTrue(inspect.Success, "The validated reopened document must be bound for subsequent commands: " + inspect.Error?.Message);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var sw = (SldWorks)application;
                        foreach (var document in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                        {
                            var path = document.GetPathName();
                            if (string.IsNullOrEmpty(path) || !IsInside(path, workspace)) continue;
                            Assert.IsFalse(IsOriginal(document, original), "Cleanup must never close the user's original document.");
                            sw.CloseDoc(document.GetTitle());
                        }
                        Assert.IsFalse((sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>()
                            .Any(document => !string.IsNullOrEmpty(document.GetPathName()) && IsInside(document.GetPathName(), workspace)),
                            "All owned managed identity test documents must be closed.");
                        RestoreAndAssertOriginal(sw, original);
                        return true;
                    }, CancellationToken.None);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run native managed identity acceptance.");
#endif
        }

        [TestMethod]
        [Timeout(180000)]
        public async Task ManagedSketchReference_ProductionCaptureAndResolveSurviveReopenRenameRebuild()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", Guid.NewGuid().ToString("N"));
            var store = new InMemoryModelReferenceStore();
            var modelId = Guid.NewGuid();
            var entityId = Guid.NewGuid();
            var executionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var record = new CadModelIdentityRecord
            {
                ModelId = modelId, DocumentKind = "Part", Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId", ConfigurationKey = "Pending",
                CurrentModelRevisionId = Guid.NewGuid(), RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now
            };
            await store.RegisterModelAsync(record, CancellationToken.None);
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace), store))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, "Start SOLIDWORKS before opting into native reference acceptance. " + status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var original = await session.InvokeWithApplicationAsync(application => CaptureOriginal((SldWorks)application), CancellationToken.None);
                var ownedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string savedPath = null;
                try
                {
                    Directory.CreateDirectory(workspace);
                    var created = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.NewPart, Parameters = new JObject(), ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(created.Success, created.Error?.Code + " " + created.Error?.Message);
                    var ownedTitle = created.Data.Value<string>("documentTitle");
                    Assert.AreNotEqual(original?.Title, ownedTitle, "The fixture must create a distinct owned document.");
                    ownedTitles.Add(ownedTitle);
                    record.ConfigurationKey = created.Data.Value<string>("configurationKey");
                    record.Status = CadModelIdentityStatus.ActiveUnsaved;
                    await store.UpdateModelAsync(record, CancellationToken.None);
                    await store.AddEntityBindingAsync(new CadEntityReferenceBinding
                    {
                        ModelId = modelId, EntityId = entityId, EntityKind = "Sketch", ConfigurationKey = record.ConfigurationKey,
                        NativeObjectKind = "SketchFeature", ReferenceFormatVersion = 3, Status = CadEntityReferenceStatus.Pending,
                        CreatedAtModelRevisionId = record.CurrentModelRevisionId, CreatedUtc = now, UpdatedUtc = now
                    }, CancellationToken.None);

                    var sketch = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }),
                        ExecutionId = executionId, ManagedModelId = modelId, OutputEntityId = entityId
                    }, CancellationToken.None);
                    Assert.IsTrue(sketch.Success, sketch.Error?.Code + " " + sketch.Error?.Message);
                    Assert.AreEqual(entityId.ToString("D"), sketch.Data.Value<string>("entityId"));
                    Assert.AreEqual(1, sketch.Data.Properties().Count(), "No native token may enter the command result.");
                    var stored = await store.GetEntityBindingAsync(modelId, entityId, record.ConfigurationKey, CancellationToken.None);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, stored.Status);
                    Assert.IsTrue(stored.NativeReferenceBytes?.Length > 0);
                    TestContext?.WriteLine("Managed sketch ID: " + entityId.ToString("D") + "; token bytes: " + stored.NativeReferenceBytes.Length);

                    var rectangle = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.AddRectangle,
                        Parameters = JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(rectangle.Success, rectangle.Error?.Code + " " + rectangle.Error?.Message);
                    var exit = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.ExitSketch, Parameters = new JObject(), ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(exit.Success, exit.Error?.Code + " " + exit.Error?.Message);
                    var beforeSave = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsTrue(beforeSave.Success, beforeSave.Error?.Code + " " + beforeSave.Error?.Message);
                    Assert.AreEqual(0, beforeSave.Data.Value<int>("nativeStatus"));

                    var save = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.SavePart,
                        Parameters = JObject.FromObject(new { path = "managed-sketch-reference.sldprt", allowOverwrite = false }),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(save.Success, save.Error?.Code + " " + save.Error?.Message);
                    savedPath = save.Data.Value<string>("path");
                    Assert.IsTrue(File.Exists(savedPath));
                    record.Status = CadModelIdentityStatus.ActiveSaved;
                    record.CanonicalPath = Path.GetFullPath(savedPath);
                    using (var stream = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        record.LastSavedSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                    await store.UpdateModelAsync(record, CancellationToken.None);
                    ownedTitles.Add(await session.InvokeWithApplicationAsync(application => ((ModelDoc2)((SldWorks)application).ActiveDoc).GetTitle(), CancellationToken.None));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);

                    var close = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = CadCommandNames.CloseDocument, Parameters = new JObject(), ExecutionId = executionId }, CancellationToken.None);
                    Assert.IsTrue(close.Success, close.Error?.Code + " " + close.Error?.Message);
                    var open = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.OpenPart, Parameters = JObject.FromObject(new { path = "managed-sketch-reference.sldprt" }), ExecutionId = executionId
                    }, CancellationToken.None);
                    Assert.IsTrue(open.Success, open.Error?.Code + " " + open.Error?.Message);
                    ownedTitles.Add(open.Data.Value<string>("documentTitle"));
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    var inspection = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.GetFeatureTree, Parameters = new JObject(), ExecutionId = executionId
                    }, CancellationToken.None);
                    Assert.IsTrue(inspection.Success, "A follow-up Host inspection must retain the reopened model binding.");
                    var reopened = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsTrue(reopened.Success, reopened.Error?.Code + " " + reopened.Error?.Message);

                    var selectionActionCalled = false;
                    var selected = await bridge.WithSelectedSketchAsync(modelId, entityId, 4, native =>
                    {
                        selectionActionCalled = true;
                        var selectedModel = (ModelDoc2)native;
                        var manager = (SelectionMgr)selectedModel.SelectionManager;
                        Assert.AreEqual(1, manager.GetSelectedObjectCount2(-1));
                        Assert.AreEqual(1, manager.GetSelectedObjectCount2(4));
                        Assert.IsNotNull(manager.GetSelectedObject6(1, 4) as Feature,
                            "The COM selection must expose the Feature interface.");
                        return CadCommandResult.Ok(new { selected = true });
                    }, CancellationToken.None);
                    Assert.IsTrue(selected.Success, selected.Error?.Code + " " + selected.Error?.Message);
                    Assert.IsTrue(selectionActionCalled);
                    Assert.AreEqual(0, await session.InvokeWithApplicationAsync(application =>
                        ((SelectionMgr)((ModelDoc2)((SldWorks)application).ActiveDoc).SelectionManager).GetSelectedObjectCount2(-1), CancellationToken.None),
                        "The sketch selection scope must leave no selection after success.");
                    var actionFailure = await bridge.WithSelectedSketchAsync(modelId, entityId, 4, native =>
                        new CadCommandResult { Success = false, Error = new CadError { Code = "TEST_ACTION_FAILED", Stage = "Execute", Message = "Deliberate test failure." } },
                        CancellationToken.None);
                    Assert.AreEqual("TEST_ACTION_FAILED", actionFailure.Error.Code);
                    Assert.AreEqual(0, await session.InvokeWithApplicationAsync(application =>
                        ((SelectionMgr)((ModelDoc2)((SldWorks)application).ActiveDoc).SelectionManager).GetSelectedObjectCount2(-1), CancellationToken.None),
                        "The sketch selection scope must leave no selection after a consumer failure.");
                    await Assert.ThrowsExceptionAsync<System.Runtime.InteropServices.COMException>(() =>
                        bridge.WithSelectedSketchAsync(modelId, entityId, 4, native =>
                            throw new System.Runtime.InteropServices.COMException("Deliberate uncertain consumer failure."), CancellationToken.None));
                    Assert.AreEqual(0, await session.InvokeWithApplicationAsync(application =>
                        ((SelectionMgr)((ModelDoc2)((SldWorks)application).ActiveDoc).SelectionManager).GetSelectedObjectCount2(-1), CancellationToken.None),
                        "An uncertain consumer exception must propagate and still clear selection.");

                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        var feature = FindSketchFeature(model);
                        Assert.IsNotNull(feature);
                        feature.Name = "ManagedSketchRenamed";
                        Assert.IsTrue(model.EditRebuild3());
                        return true;
                    }, CancellationToken.None);
                    var renamed = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsTrue(renamed.Success, renamed.Error?.Code + " " + renamed.Error?.Message);
                    Assert.AreEqual(entityId.ToString("D"), renamed.Data.Value<string>("entityId"));

                    // A display-name match in a different owned document must never satisfy this binding.
                    var lastGoodBinding = await store.GetEntityBindingAsync(modelId, entityId, record.ConfigurationKey, CancellationToken.None);
                    var lastGoodToken = lastGoodBinding.NativeReferenceBytes.ToArray();
                    var lastGoodResolutionRevision = lastGoodBinding.LastResolvedModelRevisionId;
                    var copyPath = Path.Combine(workspace, "managed-sketch-reference-copy.sldprt");
                    File.Copy(savedPath, copyPath);
                    var otherTitle = await session.InvokeWithApplicationAsync(application =>
                    {
                        var sw = (SldWorks)application;
                        var errors = 0;
                        var warnings = 0;
                        var otherModel = sw.OpenDoc6(copyPath, (int)swDocumentTypes_e.swDocPART,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings) as ModelDoc2;
                        Assert.IsNotNull(otherModel, "Open only the owned copy for the wrong-document probe.");
                        Assert.AreEqual(0, errors);
                        Assert.AreEqual(Path.GetFullPath(copyPath), Path.GetFullPath(otherModel.GetPathName()), true);
                        var sameNamed = FindSketchFeature(otherModel);
                        Assert.IsNotNull(sameNamed);
                        sameNamed.Name = "ManagedSketchRenamed";
                        Assert.AreEqual("ManagedSketchRenamed", sameNamed.Name);
                        return otherModel.GetTitle();
                    }, CancellationToken.None);
                    Assert.AreNotEqual(ownedTitle, otherTitle);
                    Assert.AreNotEqual(original?.Title, otherTitle);
                    ownedTitles.Add(otherTitle);
                    var wrongDocument = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsFalse(wrongDocument.Success);
                    Assert.AreEqual("DOCUMENT_TARGET_CHANGED", wrongDocument.Error.Code);
                    Assert.AreEqual("Resolve", wrongDocument.Error.Stage);
                    selectionActionCalled = false;
                    var wrongSelection = await bridge.WithSelectedSketchAsync(modelId, entityId, 4, native =>
                    {
                        selectionActionCalled = true;
                        return CadCommandResult.Ok(new { selected = true });
                    }, CancellationToken.None);
                    Assert.AreEqual("DOCUMENT_TARGET_CHANGED", wrongSelection.Error.Code);
                    Assert.IsFalse(selectionActionCalled);
                    var wrongFeature = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                        new CadCommandEnvelope { Command = CadCommandNames.Extrude,
                            Parameters = JObject.FromObject(new { depthMm = 5.0 }), ManagedModelId = modelId,
                            ExecutionId = executionId }, entityId), CancellationToken.None);
                    Assert.AreEqual("DOCUMENT_TARGET_CHANGED", wrongFeature.Error.Code,
                        "The v2 consumer must reject the other owned document before feature creation.");
                    var afterWrongDocument = await store.GetEntityBindingAsync(modelId, entityId, record.ConfigurationKey, CancellationToken.None);
                    CollectionAssert.AreEqual(lastGoodToken, afterWrongDocument.NativeReferenceBytes);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, afterWrongDocument.Status);
                    Assert.AreEqual(lastGoodResolutionRevision, afterWrongDocument.LastResolvedModelRevisionId);
                    TestContext?.WriteLine("Same-named wrong-document resolution rejected: " + wrongDocument.Error.Code);
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var sw = (SldWorks)application;
                        sw.CloseDoc(otherTitle);
                        var errors = 0;
                        Assert.IsNotNull(sw.ActivateDoc3(Path.GetFileName(savedPath), false,
                            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors));
                        Assert.AreEqual(0, errors);
                        return true;
                    }, CancellationToken.None);
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    var afterOther = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsTrue(afterOther.Success, afterOther.Error?.Code + " " + afterOther.Error?.Message);

                    // A byte-identical manual file copy carries the same model property, but must not rebind it.
                    var collision = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.OpenPart,
                        Parameters = JObject.FromObject(new { path = "managed-sketch-reference-copy.sldprt" }),
                        ExecutionId = executionId
                    }, CancellationToken.None);
                    Assert.IsFalse(collision.Success);
                    Assert.AreEqual("MODEL_ID_COLLISION", collision.Error.Code);
                    var copyRemainedOpen = await session.InvokeWithApplicationAsync(application =>
                        (from document in (((SldWorks)application).GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>()
                         where string.Equals(document.GetPathName(), copyPath, StringComparison.OrdinalIgnoreCase)
                         select document).Any(), CancellationToken.None);
                    Assert.IsFalse(copyRemainedOpen, "The rejected copy must not remain open or bound.");
                    await AssertActiveOwned(session, ownedTitles, workspace, original);
                    var afterCollision = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsTrue(afterCollision.Success, "The original managed model must remain bound after rejecting a duplicate ID: " +
                        afterCollision.Error?.Code + " " + afterCollision.Error?.Message);
                    TestContext?.WriteLine("Manual-copy collision rejected: " + collision.Error.Code);

                    // Delete only the test-owned sketch and verify that its old ID cannot resolve or create geometry.
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        var feature = FindSketchFeature(model);
                        Assert.IsNotNull(feature);
                        model.ClearSelection2(true);
                        Assert.IsTrue(feature.Select2(false, 0));
                        model.EditDelete();
                        Assert.IsNull(FindSketchFeature(model), "The owned sketch must actually be deleted before probing its token.");
                        return true;
                    }, CancellationToken.None);
                    var deleted = await bridge.ResolveSketchReferenceAsync(modelId, entityId, CancellationToken.None);
                    Assert.IsFalse(deleted.Success);
                    selectionActionCalled = false;
                    var deletedSelection = await bridge.WithSelectedSketchAsync(modelId, entityId, 4, native =>
                    {
                        selectionActionCalled = true;
                        return CadCommandResult.Ok(new { selected = true });
                    }, CancellationToken.None);
                    Assert.IsFalse(deletedSelection.Success);
                    Assert.IsFalse(selectionActionCalled);
                    var deletedFeature = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                        new CadCommandEnvelope { Command = CadCommandNames.Extrude,
                            Parameters = JObject.FromObject(new { depthMm = 5.0 }), ManagedModelId = modelId,
                            ExecutionId = executionId }, entityId), CancellationToken.None);
                    Assert.IsFalse(deletedFeature.Success, "The v2 consumer must not replace a deleted sketch by name or selection.");
                    Assert.AreEqual(deletedSelection.Error.Code, deletedFeature.Error.Code);
                    Assert.AreEqual(0, await session.InvokeWithApplicationAsync(application =>
                        ((SelectionMgr)((ModelDoc2)((SldWorks)application).ActiveDoc).SelectionManager).GetSelectedObjectCount2(-1), CancellationToken.None),
                        "A failed selection scope must clear stale selections.");
                    Assert.IsTrue(deleted.Error.Code == "SKETCH_REFERENCE_DELETED" ||
                        deleted.Error.Code == "SKETCH_REFERENCE_INVALID" ||
                        deleted.Error.Code == "SKETCH_REFERENCE_UNRESOLVED",
                        "Deleted sketches must fail as unresolved native references, not be rebound by name: " + deleted.Error.Code);
                    Assert.AreEqual("Resolve", deleted.Error.Stage);
                    Assert.AreEqual(1, deleted.Error.Detail.Split(new[] { "nativeStatus=" }, StringSplitOptions.None).Length - 1,
                        "The native result code must appear once in the structured diagnostic.");
                    var afterDeletion = await store.GetEntityBindingAsync(modelId, entityId, record.ConfigurationKey, CancellationToken.None);
                    CollectionAssert.AreEqual(lastGoodToken, afterDeletion.NativeReferenceBytes);
                    Assert.AreEqual(CadEntityReferenceStatus.Active, afterDeletion.Status);
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                        Assert.IsNull(FindSketchFeature(model), "Failed resolution must not recreate or select replacement geometry.");
                        return true;
                    }, CancellationToken.None);
                    TestContext?.WriteLine("Deleted-sketch resolution rejected: " + deleted.Error.Code + "; " + deleted.Error.Detail);
                    TestContext?.WriteLine("Native managed sketch reference passed; SOLIDWORKS " + status.RuntimeInfo.ReleaseYear +
                        "; saved artifact=" + savedPath + "; SHA-256=" + record.LastSavedSha256 +
                        "; manual-copy artifact=" + copyPath);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(application =>
                    {
                        var sw = (SldWorks)application;
                        try
                        {
                            foreach (var document in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                            {
                                var path = document.GetPathName();
                                var title = document.GetTitle();
                                if (string.IsNullOrEmpty(path) ? !ownedTitles.Contains(title) : !IsInside(path, workspace)) continue;
                                Assert.IsFalse(IsOriginal(document, original));
                                sw.CloseDoc(title);
                            }
                            Assert.IsFalse((sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>().Any(document =>
                                string.IsNullOrEmpty(document.GetPathName())
                                    ? ownedTitles.Contains(document.GetTitle())
                                    : IsInside(document.GetPathName(), workspace)), "All test-owned documents must be closed.");
                        }
                        finally { RestoreAndAssertOriginal(sw, original); }
                        return true;
                    }, CancellationToken.None);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies to run native reference acceptance.");
#endif
        }

        [TestMethod]
        [Timeout(180000)]
        public async Task VersionTwoExtrude_ConsumesManagedSketchAndCreatesVerifiedBoss()
        {
            RequireOptIn();
#if SOLIDWORKS_INTEROP
            var workspace = Path.Combine(@"C:\SolidWorks-CAD-Agent\Workspace\capability-tests", Guid.NewGuid().ToString("N"));
            var store = new InMemoryModelReferenceStore();
            var modelId = Guid.NewGuid();
            var sketchId = Guid.NewGuid();
            var executionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var record = new CadModelIdentityRecord
            {
                ModelId = modelId, DocumentKind = "Part", Status = CadModelIdentityStatus.Pending,
                CustomPropertyKey = "SolidWorksCadAgent.ModelId", ConfigurationKey = "Pending",
                CurrentModelRevisionId = Guid.NewGuid(), RegistryVersion = 1, CreatedUtc = now, UpdatedUtc = now
            };
            await store.RegisterModelAsync(record, CancellationToken.None);
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(workspace), store))
            {
                var status = await session.AttachAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected, "Start SOLIDWORKS before opting into native v2 extrusion acceptance. " + status.ErrorMessage);
                Assert.AreEqual(2020, status.RuntimeInfo.ReleaseYear);
                var original = await session.InvokeWithApplicationAsync(app => CaptureOriginal((SldWorks)app), CancellationToken.None);
                var ownedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Directory.CreateDirectory(workspace);
                    var created = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.NewPart, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(created.Success, created.Error?.Code + " " + created.Error?.Message);
                    var ownedTitle = created.Data.Value<string>("documentTitle");
                    Assert.AreNotEqual(original?.Title, ownedTitle);
                    ownedTitles.Add(ownedTitle);
                    record.ConfigurationKey = created.Data.Value<string>("configurationKey");
                    record.Status = CadModelIdentityStatus.ActiveUnsaved;
                    await store.UpdateModelAsync(record, CancellationToken.None);
                    await store.AddEntityBindingAsync(new CadEntityReferenceBinding
                    {
                        ModelId = modelId, EntityId = sketchId, EntityKind = "Sketch", ConfigurationKey = record.ConfigurationKey,
                        NativeObjectKind = "SketchFeature", ReferenceFormatVersion = 3, Status = CadEntityReferenceStatus.Pending,
                        CreatedAtModelRevisionId = record.CurrentModelRevisionId, CreatedUtc = now, UpdatedUtc = now
                    }, CancellationToken.None);
                    var sketch = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.CreateSketch, Parameters = JObject.FromObject(new { plane = "Top Plane" }),
                        ExecutionId = executionId, ManagedModelId = modelId, OutputEntityId = sketchId
                    }, CancellationToken.None);
                    Assert.IsTrue(sketch.Success, sketch.Error?.Code + " " + sketch.Error?.Message);
                    var rectangle = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.AddRectangle,
                        Parameters = JObject.FromObject(new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 20.0, heightMm = 10.0 }),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(rectangle.Success, rectangle.Error?.Code + " " + rectangle.Error?.Message);
                    var exit = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.ExitSketch, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(exit.Success, exit.Error?.Code + " " + exit.Error?.Message);

                    var result = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                        new CadCommandEnvelope
                        {
                            Command = CadCommandNames.Extrude,
                            Parameters = JObject.FromObject(new { depthMm = 5.0 }),
                            ExecutionId = executionId, ManagedModelId = modelId
                        }, sketchId), CancellationToken.None);
                    Assert.IsTrue(result.Success, result.Error?.Code + " " + result.Error?.Message + " " + result.Error?.Detail);
                    Assert.AreEqual(5.0, result.Data.Value<double>("depthMm"));
                    Assert.IsTrue((await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.Rebuild, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None)).Success);
                    var body = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.GetBodyCount, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(body.Success);
                    Assert.AreEqual(1, body.Data.Value<int>("bodyCount"));
                    var bounds = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.GetBoundingBox, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(bounds.Success);
                    var extents = new[] { bounds.Data.Value<double>("sizeXmm"), bounds.Data.Value<double>("sizeYmm"),
                        bounds.Data.Value<double>("sizeZmm") }.OrderBy(value => value).ToArray();
                    var expectedExtents = new[] { 5.0, 10.0, 20.0 };
                    for (var axis = 0; axis < 3; axis++)
                        Assert.AreEqual(expectedExtents[axis], extents[axis], 0.02,
                            "The v2 boss must have the requested three physical dimensions.");
                    var native = await session.InvokeWithApplicationAsync(app =>
                    {
                        var model = (ModelDoc2)((SldWorks)app).ActiveDoc;
                        var bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                        Assert.AreEqual(1, bodies?.Length);
                        var mass = ((Body2)bodies[0]).GetMassProperties(1.0) as double[];
                        Assert.IsNotNull(mass);
                        var selectionCount = ((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1);
                        return (volumeMm3: mass[3] * 1e9, selectionCount,
                            featureTypes: string.Join(",", EnumerateFeatureTypes(model)));
                    }, CancellationToken.None);
                    Assert.AreEqual(1000.0, native.volumeMm3, 0.05);
                    Assert.AreEqual(0, native.selectionCount, "The v2 consumer must clear temporary selection after feature creation.");
                    Assert.IsTrue(native.featureTypes.Contains("Extrusion") || native.featureTypes.Contains("Boss"),
                        "The native feature tree must contain a boss: " + native.featureTypes);
                    var errors = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.GetRebuildErrors, Parameters = new JObject(),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(errors.Success);
                    Assert.IsFalse(errors.Data.Value<bool>("hasErrors"));
                    var save = await bridge.ExecuteAsync(new CadCommandEnvelope
                    {
                        Command = CadCommandNames.SavePart,
                        Parameters = JObject.FromObject(new { path = "v2-managed-sketch-extrude.sldprt", allowOverwrite = false }),
                        ExecutionId = executionId, ManagedModelId = modelId
                    }, CancellationToken.None);
                    Assert.IsTrue(save.Success, save.Error?.Code + " " + save.Error?.Message);
                    var path = save.Data.Value<string>("path");
                    Assert.IsTrue(File.Exists(path));
                    string hash;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                    var otherExecution = await bridge.ExecuteVersionedAsync(new CadVersionedCommandRequest(2, 2,
                        new CadCommandEnvelope
                        {
                            Command = CadCommandNames.Extrude,
                            Parameters = JObject.FromObject(new { depthMm = 2.0 }),
                            ExecutionId = Guid.NewGuid(), ManagedModelId = modelId
                        }, sketchId), CancellationToken.None);
                    Assert.IsFalse(otherExecution.Success, "A different job must not consume a bound sketch.");
                    Assert.AreEqual("SKETCH_REFERENCE_CONTEXT_MISMATCH", otherExecution.Error.Code);
                    var afterWrongExecution = await session.InvokeWithApplicationAsync(app =>
                    {
                        var model = (ModelDoc2)((SldWorks)app).ActiveDoc;
                        return (bossCount: EnumerateFeatureTypes(model).Count(type => type == "Extrusion" || type == "Boss"),
                            selectionCount: ((SelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1));
                    }, CancellationToken.None);
                    Assert.AreEqual(1, afterWrongExecution.bossCount, "The rejected job must not add a feature.");
                    Assert.AreEqual(0, afterWrongExecution.selectionCount);
                    TestContext?.WriteLine("SOLIDWORKS " + status.RuntimeInfo.ReleaseYear + " native v2 Extrude; modelId=" + modelId +
                        "; sketchId=" + sketchId + "; artifact=" + path + "; SHA-256=" + hash +
                        "; featureTypes=" + native.featureTypes);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(app =>
                    {
                        var sw = (SldWorks)app;
                        try
                        {
                            foreach (var document in (sw.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<ModelDoc2>())
                            {
                                var path = document.GetPathName();
                                var title = document.GetTitle();
                                if (string.IsNullOrEmpty(path) ? !ownedTitles.Contains(title) : !IsInside(path, workspace)) continue;
                                Assert.IsFalse(IsOriginal(document, original));
                                sw.CloseDoc(title);
                            }
                        }
                        finally { RestoreAndAssertOriginal(sw, original); }
                        return true;
                    }, CancellationToken.None);
                }
            }
#else
            await Task.CompletedTask;
            Assert.Inconclusive("Build with installed SOLIDWORKS interop assemblies for native v2 Extrude acceptance.");
#endif
        }

        private static void RequireOptIn()
        {
            if (System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS") != "1")
                Assert.Inconclusive("Set SOLIDWORKS_RUN_REFERENCE_TESTS=1 and SOLIDWORKS_EXPECTED_YEAR=2020 to opt into native reference acceptance.");
            Assert.AreEqual("2020", System.Environment.GetEnvironmentVariable("SOLIDWORKS_EXPECTED_YEAR"), "This native reference probe is scoped to SOLIDWORKS 2020.");
        }

#if SOLIDWORKS_INTEROP
        private static void AssertSketchReferenceResolves(ModelDoc2 model, byte[] reference, string stage)
        {
            Assert.IsNotNull(reference, "The persistent sketch reference must have been captured before " + stage + ".");
            var nativeStatus = -1;
            var resolved = model.Extension.GetObjectByPersistReference3(reference, out nativeStatus);
            Assert.AreEqual((int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok, nativeStatus,
                "SOLIDWORKS native resolution status during " + stage + ".");
            Assert.IsNotNull(resolved, "SOLIDWORKS must return an object during " + stage + ".");
            var sketch = resolved as Sketch;
            var feature = resolved as Feature;
            Console.WriteLine("Persistent reference " + stage + ": nativeStatus=" + nativeStatus + ", sketchInterface=" + (sketch != null) + ", featureInterface=" + (feature != null));
            if (sketch != null) return;
            Assert.IsNotNull(feature, "The reference must resolve to a sketch or its native feature during " + stage + ".");
            Assert.AreEqual("ProfileFeature", feature.GetTypeName2(), "The resolved native feature must be a sketch feature during " + stage + ".");
            Assert.IsNotNull(feature.GetSpecificFeature2() as Sketch, "The resolved feature must expose its Sketch specific-feature interface during " + stage + ".");
        }

        private static Feature FindSketchFeature(ModelDoc2 model)
        {
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
            {
                if (feature.GetTypeName2() == "ProfileFeature") return feature;
            }
            return null;
        }

        private static IEnumerable<string> EnumerateFeatureTypes(ModelDoc2 model)
        {
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                yield return feature.GetTypeName2();
        }

        private static OriginalDocument CaptureOriginal(SldWorks sw)
        {
            var model = sw.ActiveDoc as ModelDoc2;
            return model == null ? null : new OriginalDocument { Title = model.GetTitle(), Path = model.GetPathName(), SaveFlag = model.GetSaveFlag() };
        }

        private static bool IsOriginal(ModelDoc2 model, OriginalDocument original) => original != null && model.GetTitle() == original.Title && model.GetPathName() == original.Path;

        private static bool IsInside(string path, string workspace) => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void RestoreAndAssertOriginal(SldWorks sw, OriginalDocument original)
        {
            if (original == null) return;
            var documents = sw.GetDocuments() as object[] ?? Array.Empty<object>();
            var open = documents.OfType<ModelDoc2>().SingleOrDefault(model => IsOriginal(model, original));
            Assert.IsNotNull(open, "The user's original document must remain open with its original title and path. Expected=" +
                original.Title + " | " + original.Path + "; open=" +
                string.Join("; ", documents.OfType<ModelDoc2>().Select(model => model.GetTitle() + " | " + model.GetPathName())));
            Assert.AreEqual(original.Path, open.GetPathName());
            Assert.AreEqual(original.SaveFlag, open.GetSaveFlag(), "The original document save flag must not change.");
            var errors = 0;
            var activated = sw.ActivateDoc3(original.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as ModelDoc2;
            Assert.IsNotNull(activated, "Restore the user's original active document without rebuilding it.");
            Assert.IsTrue(IsOriginal(activated, original));
            Assert.AreEqual(original.SaveFlag, activated.GetSaveFlag());
        }

        private static Task<bool> AssertActiveOwned(SolidWorksSession session, HashSet<string> ownedTitles, string workspace, OriginalDocument original) =>
            session.InvokeWithApplicationAsync(application =>
            {
                var model = (ModelDoc2)((SldWorks)application).ActiveDoc;
                Assert.IsNotNull(model);
                Assert.IsTrue(ownedTitles.Contains(model.GetTitle()) && IsInside(model.GetPathName(), workspace),
                    "The active document must be the test-owned model inside its isolated workspace.");
                Assert.IsFalse(IsOriginal(model, original));
                return true;
            }, CancellationToken.None);

        private sealed class OriginalDocument
        {
            public string Title;
            public string Path;
            public bool SaveFlag;
        }

        private sealed class InMemoryModelReferenceStore : IModelReferenceStore
        {
            private readonly Dictionary<Guid, CadModelIdentityRecord> _models = new Dictionary<Guid, CadModelIdentityRecord>();
            private readonly Dictionary<Guid, CadEntityReferenceBinding> _bindings = new Dictionary<Guid, CadEntityReferenceBinding>();
            public Task RegisterModelAsync(CadModelIdentityRecord model, CancellationToken token)
            {
                _models.Add(model.ModelId, model);
                return Task.CompletedTask;
            }
            public Task<CadModelIdentityRecord> GetModelAsync(Guid modelId, CancellationToken token) => Task.FromResult(_models.TryGetValue(modelId, out var model) ? model : null);
            public Task<CadModelIdentityRecord> FindModelByCanonicalPathAsync(string canonicalPath, CancellationToken token) =>
                Task.FromResult(_models.Values.SingleOrDefault(model => string.Equals(model.CanonicalPath, Path.GetFullPath(canonicalPath), StringComparison.OrdinalIgnoreCase)));
            public Task UpdateModelAsync(CadModelIdentityRecord model, CancellationToken token) { _models[model.ModelId] = model; return Task.CompletedTask; }
            public Task AddEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token) { _bindings.Add(binding.EntityId, binding); return Task.CompletedTask; }
            public Task<CadEntityReferenceBinding> GetEntityBindingAsync(Guid modelId, Guid entityId, string configurationKey, CancellationToken token) =>
                Task.FromResult(_bindings.TryGetValue(entityId, out var binding) && binding.ModelId == modelId && binding.ConfigurationKey == configurationKey ? binding : null);
            public Task UpdateEntityBindingAsync(CadEntityReferenceBinding binding, CancellationToken token) { _bindings[binding.EntityId] = binding; return Task.CompletedTask; }
        }
#endif

        private static async Task<CadCommandResult> Execute(SolidWorksBridgeFacade bridge, string command, object parameters)
        {
            var result = await bridge.ExecuteAsync(new CadCommandEnvelope { Command = command, Parameters = JObject.FromObject(parameters) }, CancellationToken.None);
            Assert.IsTrue(result.Success, command + ": " + result.Error?.Code + " " + result.Error?.Message);
            return result;
        }
    }
}
