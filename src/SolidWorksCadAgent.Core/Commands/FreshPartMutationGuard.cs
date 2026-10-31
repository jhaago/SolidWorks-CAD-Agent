using System;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Trusted document-binding state; never populated from provider/command JSON.</summary>
    public sealed class FreshPartMutationGuard
    {
        private bool _created,_quarantined;
        public void BindCreated(){_created=true;_quarantined=false;}
        public void BindExisting(){_created=false;_quarantined=false;}
        public void Quarantine(){_quarantined=true;}
        public void RequireUnsaved(string nativePath)
        {
            if(!_created)throw new InvalidOperationException("Finishing requires a part created by this bridge; opened/existing parts require checkpointed editing.");
            if(_quarantined)throw new InvalidOperationException("Finishing is quarantined after an uncertain native failure. Create a new part; do not replay the mutation.");
            if(nativePath==null||nativePath.Length!=0)throw new InvalidOperationException("Finishing requires a verified unsaved part; saved or unknown-path parts require checkpointed editing.");
        }
    }
}
