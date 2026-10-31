using System;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public enum CadModelIdentityStatus
    {
        Pending = 0,
        ActiveUnsaved = 1,
        ActiveSaved = 2,
        IdentityCollision = 3,
        NeedsRegistration = 4,
        Uncertain = 5
    }

    public enum CadEntityReferenceStatus
    {
        Pending = 0,
        Active = 1,
        Suppressed = 2,
        Deleted = 3,
        Stale = 4,
        Ambiguous = 5,
        Unresolved = 6,
        Uncertain = 7
    }

    public sealed class CadModelIdentityRecord
    {
        public Guid ModelId { get; set; }
        public Guid? ParentModelId { get; set; }
        public string DocumentKind { get; set; }
        public CadModelIdentityStatus Status { get; set; }
        public string CustomPropertyKey { get; set; }
        public string CanonicalPath { get; set; }
        public string LastSavedSha256 { get; set; }
        public Guid CurrentModelRevisionId { get; set; }
        public string ConfigurationKey { get; set; }
        public string SolidWorksRevision { get; set; }
        public int RegistryVersion { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class CadEntityReferenceBinding
    {
        public Guid ModelId { get; set; }
        public Guid EntityId { get; set; }
        public string EntityKind { get; set; }
        public string ConfigurationKey { get; set; }
        public string NativeObjectKind { get; set; }
        public int ReferenceFormatVersion { get; set; }
        public byte[] NativeReferenceBytes { get; set; }
        public Guid CreatedAtModelRevisionId { get; set; }
        public Guid? LastResolvedModelRevisionId { get; set; }
        public string SemanticFingerprintJson { get; set; }
        public CadEntityReferenceStatus Status { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
