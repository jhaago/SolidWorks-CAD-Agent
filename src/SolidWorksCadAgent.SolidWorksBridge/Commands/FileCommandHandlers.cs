using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Workspace;
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

            var parent = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (model == null)
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Save", "No active SOLIDWORKS document is available to save.");
                }

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

        public OpenPartCommandHandler(ISolidWorksSession session, WorkspacePolicy workspacePolicy)
            : base(session)
        {
            _workspacePolicy = workspacePolicy ?? throw new ArgumentNullException(nameof(workspacePolicy));
        }

        public override string Name => CadCommandNames.OpenPart;

        public override CadError Validate(JObject parameters)
        {
            return string.IsNullOrWhiteSpace(GetString(parameters, "path"))
                ? InvalidParameter("path is required.")
                : null;
        }

        public override Task<CadCommandResult> ExecuteAsync(
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
                return Task.FromResult(Failure(
                    "WORKSPACE_POLICY_VIOLATION",
                    "Validate",
                    "The requested open path is not permitted.",
                    ex.Message));
            }

            return InvokeAsync(application =>
            {
#if SOLIDWORKS_INTEROP
                var swApp = application as SldWorks;
                if (swApp == null)
                {
                    return Failure("SOLIDWORKS_APPLICATION_INVALID", "Open", "The connected SOLIDWORKS application object is invalid.");
                }

                var errors = 0;
                var warnings = 0;
                var model = swApp.OpenDoc6(
                    resolvedPath,
                    (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    string.Empty,
                    ref errors,
                    ref warnings) as ModelDoc2;

                if (model == null || errors != 0)
                {
                    return Failure(
                        "OPEN_FAILED",
                        "Open",
                        "SOLIDWORKS did not open the requested part successfully.",
                        string.Format("errors={0}; warnings={1}; path={2}", errors, warnings, resolvedPath));
                }

                return Ok(new { path = resolvedPath, documentTitle = model.GetTitle(), errors, warnings });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
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
                var model = swApp?.ActiveDoc as ModelDoc2;
                if (swApp == null || model == null)
                {
                    return Failure("NO_ACTIVE_DOCUMENT", "Close", "No active SOLIDWORKS document is available to close.");
                }

                var title = model.GetTitle();
                swApp.CloseDoc(title);
                return Ok(new { documentTitle = title, closed = true });
#else
                return InteropUnavailable();
#endif
            }, cancellationToken);
        }
    }
}
