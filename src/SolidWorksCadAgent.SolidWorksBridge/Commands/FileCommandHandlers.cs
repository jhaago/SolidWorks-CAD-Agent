using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public sealed class SavePartCommandHandler : SolidWorksCommandHandlerBase
    {
        private readonly WorkspacePolicy _workspacePolicy;

        public SavePartCommandHandler(ISolidWorksSession session, WorkspacePolicy workspacePolicy)
            : base(session)
        {
            _workspacePolicy = workspacePolicy ?? throw new ArgumentNullException(nameof(workspacePolicy));
        }

        public override string Name => CadCommandNames.SavePart;

        public override CadError Validate(JObject parameters)
        {
            if (string.IsNullOrWhiteSpace(GetString(parameters, "path")))
            {
                return InvalidParameter("path is required.");
            }

            var overwrite = parameters?["allowOverwrite"];
            if (overwrite != null && overwrite.Type != JTokenType.Boolean)
            {
                return InvalidParameter("allowOverwrite must be a boolean when supplied.");
            }

            return null;
        }

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var requestedPath = GetString(parameters, "path");
            var allowOverwrite = parameters?["allowOverwrite"]?.Value<bool>() ?? false;

            string resolvedPath;
            try
            {
                resolvedPath = _workspacePolicy.ResolveForWrite(requestedPath, allowOverwrite);
            }
            catch (WorkspacePolicyException ex)
            {
                return Task.FromResult(Failure(
                    "WORKSPACE_POLICY_VIOLATION",
                    "Validate",
                    "The requested save path is not permitted.",
                    ex.Message));
            }

            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (model == null)
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Save", "No active SOLIDWORKS document is available to save.");
                }

                // Recheck on the STA after queueing, before creating directories or saving.
                _workspacePolicy.ResolveForWrite(resolvedPath, allowOverwrite);
                var parent = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                _workspacePolicy.ResolveForWrite(resolvedPath, allowOverwrite);
                model.ClearSelection2(true);
                var errors = 0;
                var warnings = 0;
                var saved = model.Extension.SaveAs(
                    resolvedPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    ref errors,
                    ref warnings);

                if (!saved || errors != 0)
                {
                    return Failure(
                        "SAVE_FAILED",
                        "Save",
                        "SOLIDWORKS did not save the part successfully.",
                        string.Format("errors={0}; warnings={1}; path={2}", errors, warnings, resolvedPath));
                }

                if (!File.Exists(resolvedPath))
                {
                    return Failure(
                        "SAVE_OUTPUT_MISSING",
                        "Save",
                        "SOLIDWORKS reported success but the output file was not found.",
                        resolvedPath);
                }

                return Ok(new { path = resolvedPath, errors, warnings });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }

    public sealed class OpenPartCommandHandler : SolidWorksCommandHandlerBase
    {
        private readonly WorkspacePolicy _workspacePolicy;
        private readonly IModelReferenceStore _modelReferenceStore;

        public OpenPartCommandHandler(ISolidWorksSession session, WorkspacePolicy workspacePolicy)
            : this(session, workspacePolicy, null)
        {
        }

        public OpenPartCommandHandler(ISolidWorksSession session, WorkspacePolicy workspacePolicy, IModelReferenceStore modelReferenceStore)
            : base(session)
        {
            _workspacePolicy = workspacePolicy ?? throw new ArgumentNullException(nameof(workspacePolicy));
            _modelReferenceStore = modelReferenceStore;
        }

        public override string Name => CadCommandNames.OpenPart;

        public override CadError Validate(JObject parameters)
        {
            return string.IsNullOrWhiteSpace(GetString(parameters, "path"))
                ? InvalidParameter("path is required.")
                : null;
        }

        public override async Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var requestedPath = GetString(parameters, "path");
            string resolvedPath;
            try
            {
                resolvedPath = _workspacePolicy.ResolveForRead(requestedPath);
            }
            catch (WorkspacePolicyException ex)
            {
                return Failure(
                    "WORKSPACE_POLICY_VIOLATION",
                    "Validate",
                    "The requested open path is not permitted.",
                    ex.Message);
            }

            OpenProbe probe;
            try
            {
                probe = await Session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var swApp = application as SldWorks;
                    if (swApp == null)
                        return OpenProbe.Failed("SOLIDWORKS_APPLICATION_INVALID", "The connected SOLIDWORKS application object is invalid.");

                    var prior = swApp.ActiveDoc;
                    var wasOpen = (swApp.GetDocuments() as object[] ?? System.Array.Empty<object>())
                        .OfType<ModelDoc2>().Any(document => SamePath(document.GetPathName(), resolvedPath));

                    var errors = 0;
                    var warnings = 0;
                    _workspacePolicy.ResolveForRead(resolvedPath);
                    var model = swApp.OpenDoc6(
                        resolvedPath,
                        (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        string.Empty,
                        ref errors,
                        ref warnings) as ModelDoc2;

                    if (model != null)
                        DocumentContext(Session).StageOpen(model, prior, !wasOpen);
                    if (model == null || errors != 0)
                        return OpenProbe.Failed("OPEN_FAILED", "SOLIDWORKS did not open the requested part successfully.",
                            string.Format("errors={0}; warnings={1}; path={2}", errors, warnings, resolvedPath), model != null);

                    string value = null;
                    var getResult = model.Extension.CustomPropertyManager[""].Get6(
                        "SolidWorksCadAgent.ModelId", false, out value, out _, out _, out _);
                    var propertyPresent = getResult != (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent;
                    return new OpenProbe
                    {
                        Success = true,
                        Path = resolvedPath,
                        Title = model.GetTitle(),
                        Errors = errors,
                        Warnings = warnings,
                        PropertyPresent = propertyPresent,
                        PropertyValue = value
                    };
#else
                    return OpenProbe.Failed("SOLIDWORKS_INTEROP_UNAVAILABLE", "This build was compiled without the installed SOLIDWORKS interop libraries.");
#endif
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspacePolicyException ex)
            {
                return Failure("WORKSPACE_POLICY_VIOLATION", "Open", "The requested open path is no longer permitted.", ex.Message);
            }
            catch (DocumentTargetException ex)
            {
                return Failure("DOCUMENT_TARGET_CHANGED", "Open", ex.Message);
            }
            catch (Exception ex)
            {
                return await RejectOpenAsync("OPEN_FAILED", "SOLIDWORKS failed while opening or reading the managed document identity.", resolvedPath,
                    CancellationToken.None, ex.Message).ConfigureAwait(false);
            }

            if (!probe.Success)
            {
                if (probe.Staged)
                    return await RejectOpenAsync(probe.ErrorCode, probe.ErrorMessage, probe.Path ?? resolvedPath, CancellationToken.None, probe.ErrorDetail).ConfigureAwait(false);
                return Failure(probe.ErrorCode, "Open", probe.ErrorMessage, probe.ErrorDetail);
            }

            try
            {
                CadModelIdentityRecord managedModel = null;
                if (probe.PropertyPresent)
                {
                    if (!Guid.TryParse(probe.PropertyValue, out var stampedModelId))
                        return await RejectOpenAsync("MODEL_ID_PROPERTY_INVALID", "The part contains an invalid managed model ID property.", probe.Path, cancellationToken).ConfigureAwait(false);
                    if (_modelReferenceStore == null)
                        return await RejectOpenAsync("MODEL_REFERENCE_STORE_UNAVAILABLE", "The part is marked as Agent-managed but the reference registry is unavailable.", probe.Path, cancellationToken).ConfigureAwait(false);
                    managedModel = await _modelReferenceStore.GetModelAsync(stampedModelId, cancellationToken).ConfigureAwait(false);
                    if (managedModel == null)
                        return await RejectOpenAsync("MODEL_ID_UNREGISTERED", "The part's managed model ID is not present in the local registry.", probe.Path, cancellationToken).ConfigureAwait(false);
                }
                else if (_modelReferenceStore != null)
                {
                    try
                    {
                        managedModel = await _modelReferenceStore.FindModelByCanonicalPathAsync(probe.Path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return await RejectOpenAsync("MODEL_ID_PATH_AMBIGUOUS", "More than one managed model is registered at this path.", probe.Path, cancellationToken, ex.Message).ConfigureAwait(false);
                    }
                    if (managedModel != null)
                        return await RejectOpenAsync("MODEL_ID_PROPERTY_MISSING", "The registry identifies this path as managed, but its model ID property is missing.", probe.Path, cancellationToken).ConfigureAwait(false);
                }

                if (managedModel != null)
                {
                    var currentHash = ComputeSha256(probe.Path);
                    if (managedModel.Status != CadModelIdentityStatus.ActiveSaved || string.IsNullOrWhiteSpace(managedModel.CanonicalPath))
                        return await RejectOpenAsync("MODEL_ID_NOT_OPENABLE", "The managed model is not in a saved, active registry state.", probe.Path, cancellationToken).ConfigureAwait(false);
                    var oldPath = Path.GetFullPath(managedModel.CanonicalPath);
                    var samePath = string.Equals(oldPath, Path.GetFullPath(probe.Path), StringComparison.OrdinalIgnoreCase);
                    if (!samePath && File.Exists(oldPath))
                        return await RejectOpenAsync("MODEL_ID_COLLISION", "Another file still exists at the registered path for this managed model ID.", probe.Path, cancellationToken, oldPath).ConfigureAwait(false);
                    if (!string.Equals(currentHash, managedModel.LastSavedSha256, StringComparison.OrdinalIgnoreCase))
                        return await RejectOpenAsync("MODEL_ID_HASH_MISMATCH", "The file content does not match the last registered save for this managed model.", probe.Path, cancellationToken).ConfigureAwait(false);
                    if (!samePath)
                    {
                        managedModel.CanonicalPath = Path.GetFullPath(probe.Path);
                        managedModel.UpdatedUtc = DateTime.UtcNow;
                        await _modelReferenceStore.UpdateModelAsync(managedModel, cancellationToken).ConfigureAwait(false);
                    }
                }

                var bindError = await Session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var context = DocumentContext(Session);
                    var active = ((SldWorks)application).ActiveDoc;
                    if (!context.IsPendingOpenActive(active))
                    {
                        context.RejectPendingOpen(document => ((SldWorks)application).CloseDoc(((ModelDoc2)document).GetTitle()),
                            document => Activate((SldWorks)application, (ModelDoc2)document));
                        return "DOCUMENT_TARGET_CHANGED";
                    }
                    context.BindPendingOpen(active, managedModel?.ModelId);
                    return null;
#else
                    return "SOLIDWORKS_INTEROP_UNAVAILABLE";
#endif
                }, cancellationToken).ConfigureAwait(false);
                if (bindError != null) return Failure(bindError, "Open", "The opened document changed before its identity could be bound.");
                return Ok(new { path = probe.Path, documentTitle = probe.Title, errors = probe.Errors, warnings = probe.Warnings });
            }
            catch (OperationCanceledException)
            {
                await RejectOpenAsync("OPEN_CANCELLED", "Opening the document was cancelled before identity validation completed.", probe.Path, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                return await RejectOpenAsync("MODEL_ID_VALIDATION_FAILED", "The opened document could not be safely validated against the identity registry.", probe.Path, CancellationToken.None, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task<CadCommandResult> RejectOpenAsync(string code, string message, string path, CancellationToken token, string detail = null)
        {
            try
            {
                await Session.InvokeWithApplicationAsync(application =>
                {
#if SOLIDWORKS_INTEROP
                    var sw = (SldWorks)application;
                    DocumentContext(Session).RejectPendingOpen(
                        document => sw.CloseDoc(((ModelDoc2)document).GetTitle()),
                        document => Activate(sw, (ModelDoc2)document));
#endif
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            return Failure(code, "Open", message, detail ?? path);
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }

#if SOLIDWORKS_INTEROP
        private static void Activate(SldWorks application, ModelDoc2 document)
        {
            if (document == null) return;
            var errors = 0;
            application.ActivateDoc3(document.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
        }
#endif

        private sealed class OpenProbe
        {
            public bool Success { get; set; }
            public string ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorDetail { get; set; }
            public string Path { get; set; }
            public string Title { get; set; }
            public int Errors { get; set; }
            public int Warnings { get; set; }
            public bool PropertyPresent { get; set; }
            public string PropertyValue { get; set; }
            public bool Staged { get; set; }
            public static OpenProbe Failed(string code, string message, string detail = null, bool staged = false) =>
                new OpenProbe { ErrorCode = code, ErrorMessage = message, ErrorDetail = detail, Staged = staged };
        }
    }

    public sealed class CloseDocumentCommandHandler : SolidWorksCommandHandlerBase
    {
        public CloseDocumentCommandHandler(ISolidWorksSession session) : base(session) { }

        public override string Name => CadCommandNames.CloseDocument;

        public override CadError Validate(JObject parameters) => null;

        public override Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken)
        {
            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = RequireDocument(application) as ModelDoc2;
                if (swApp == null || model == null)
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Close", "No active SOLIDWORKS document is available to close.");
                }

                var title = model.GetTitle();
                swApp.CloseDoc(title);
                ClearDocument();
                return Ok(new { documentTitle = title, closed = true });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
