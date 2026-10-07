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
        private object _pendingOpenDocument;
        private object _previousActiveDocument;
        private bool _pendingOpenWasOwned;
        private Guid? _executionId;
        public Guid? ManagedModelId { get; private set; }
        public Guid? OutputEntityId { get; private set; }
        public void BeginExecution(Guid? executionId)
        {
            if (_executionId != executionId) Clear();
            _executionId = executionId;
        }
        public void SetExecutionIdentity(Guid? managedModelId, Guid? outputEntityId)
        {
            ManagedModelId = managedModelId;
            OutputEntityId = outputEntityId;
        }
        public void Bind(object document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _pendingOpenDocument = null;
            _previousActiveDocument = null;
            _pendingOpenWasOwned = false;
        }
        public void StageOpen(object document, object previousActiveDocument, bool openedByAgent)
        {
            _pendingOpenDocument = document ?? throw new ArgumentNullException(nameof(document));
            _previousActiveDocument = previousActiveDocument;
            _pendingOpenWasOwned = openedByAgent;
        }
        public object PendingOpenDocument => _pendingOpenDocument;
        public bool IsPendingOpenActive(object active) => _pendingOpenDocument != null && active != null && SameIdentity(_pendingOpenDocument, active);
        public void BindPendingOpen(object active, Guid? managedModelId)
        {
            if (!IsPendingOpenActive(active)) throw new DocumentTargetException();
            _document = _pendingOpenDocument;
            ManagedModelId = managedModelId;
            OutputEntityId = null;
            _pendingOpenDocument = null;
            _previousActiveDocument = null;
            _pendingOpenWasOwned = false;
        }
        public void RejectPendingOpen(Action<object> closeOwnedDocument, Action<object> restorePreviousDocument)
        {
            if (_pendingOpenDocument != null && _pendingOpenWasOwned) closeOwnedDocument?.Invoke(_pendingOpenDocument);
            if (_previousActiveDocument != null) restorePreviousDocument?.Invoke(_previousActiveDocument);
            _pendingOpenDocument = null;
            _previousActiveDocument = null;
            _pendingOpenWasOwned = false;
        }
        public void Clear()
        {
            _document = null;
            _pendingOpenDocument = null;
            _previousActiveDocument = null;
            _pendingOpenWasOwned = false;
            ManagedModelId = null;
            OutputEntityId = null;
        }
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
