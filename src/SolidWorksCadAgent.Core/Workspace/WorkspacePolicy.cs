using System;
using System.Collections.Generic;
using System.IO;

namespace SolidWorksCadAgent.Core.Workspace
{
    public sealed class WorkspacePolicyException : Exception
    {
        public WorkspacePolicyException(string message) : base(message)
        {
        }

        public WorkspacePolicyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class WorkspacePolicy
    {
        private static readonly HashSet<string> AllowedCadExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".sldprt"
            };

        private readonly string _root;
        private readonly string _rootWithSeparator;

        public WorkspacePolicy(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new WorkspacePolicyException("Workspace root cannot be empty.");
            }

            try
            {
                _root = Path.GetFullPath(workspaceRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new WorkspacePolicyException("Workspace root is not a valid path.", ex);
            }

            if (string.IsNullOrEmpty(_root))
            {
                throw new WorkspacePolicyException("Workspace root cannot resolve to an empty path.");
            }

            _rootWithSeparator = _root + Path.DirectorySeparatorChar;
        }

        public string ResolveForRead(string requestedPath)
        {
            var resolved = ResolveCadPath(requestedPath);
            if (!File.Exists(resolved))
            {
                throw new WorkspacePolicyException("Requested CAD file does not exist inside the workspace.");
            }

            return resolved;
        }

        public string ResolveForWrite(string requestedPath, bool allowOverwrite)
        {
            var resolved = ResolveCadPath(requestedPath);
            if (File.Exists(resolved) && !allowOverwrite)
            {
                throw new WorkspacePolicyException("Target file already exists and overwrite approval was not supplied.");
            }

            return resolved;
        }

        private string ResolveCadPath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw new WorkspacePolicyException("A CAD filename is required.");
            }

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(requestedPath)
                    ? Path.GetFullPath(requestedPath)
                    : Path.GetFullPath(Path.Combine(_root, requestedPath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new WorkspacePolicyException("Requested CAD path is invalid.", ex);
            }

            if (!resolved.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspacePolicyException("Requested path is outside the configured workspace.");
            }

            var fileName = Path.GetFileName(resolved);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new WorkspacePolicyException("A CAD filename is required.");
            }

            var extension = Path.GetExtension(resolved);
            if (!AllowedCadExtensions.Contains(extension))
            {
                throw new WorkspacePolicyException("The requested file extension is not permitted for CAD file operations.");
            }

            return resolved;
        }
    }
}
