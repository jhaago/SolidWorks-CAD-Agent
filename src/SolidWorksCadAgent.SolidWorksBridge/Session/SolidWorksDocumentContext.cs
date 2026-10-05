using System;
using System.Runtime.InteropServices;

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    internal sealed class DocumentTargetException : Exception
    {
        public DocumentTargetException() : base("The active SOLIDWORKS document differs from the document opened or created for this execution. Restore that document and submit a new job.") { }
    }

    // Accessed only inside the session STA callback. Never compare mutable titles or paths.
    internal sealed class SolidWorksDocumentContext
    {
        private object _document;
        private Guid? _executionId;
        public void BeginExecution(Guid? executionId)
        {
            if (_executionId != executionId) Clear();
            _executionId = executionId;
        }
        public void Bind(object document) => _document = document ?? throw new ArgumentNullException(nameof(document));
        public void Clear() => _document = null;
        public object RequireActive(object active)
        {
            if (_document == null || active == null || !SameIdentity(_document, active)) throw new DocumentTargetException();
            return _document;
        }
        private static bool SameIdentity(object expected, object active)
        {
            if (ReferenceEquals(expected, active)) return true;
            if (!Marshal.IsComObject(expected) || !Marshal.IsComObject(active)) return false;
            var expectedIdentity = Marshal.GetIUnknownForObject(expected);
            try
            {
                var activeIdentity = Marshal.GetIUnknownForObject(active);
                try { return expectedIdentity == activeIdentity; }
                finally { Marshal.Release(activeIdentity); }
            }
            finally { Marshal.Release(expectedIdentity); }
        }
    }
}
