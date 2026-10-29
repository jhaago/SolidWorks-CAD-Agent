using System;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    public sealed class FeatureMaterialVerificationException : Exception
    {
        internal FeatureMaterialVerificationException(Exception inner, bool attempted, SolidMaterialSnapshot before, SolidMaterialSnapshot after)
            : base(inner.Message, inner) { MutationAttempted = attempted; Before = before; After = after; }
        public bool MutationAttempted { get; }
        public SolidMaterialSnapshot Before { get; }
        public SolidMaterialSnapshot After { get; }
    }

    public sealed class FeatureMaterialOutcome<T> where T : class
    {
        internal FeatureMaterialOutcome(T feature, MaterialChangeEvidence evidence) { Feature = feature; Evidence = evidence; }
        public T Feature { get; }
        public MaterialChangeEvidence Evidence { get; }
    }

    /// <summary>Runs synchronously on the caller's STA. Never retries a mutation or promises rollback.</summary>
    public static class FeatureMaterialOperation
    {
        public static FeatureMaterialOutcome<T> Run<T>(MaterialChangeKind kind, Func<SolidMaterialSnapshot> measure,
            Func<T> create, Action<T> inspect, Action<T, MaterialChangeEvidence> finalizeResult = null) where T : class
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));
            if (create == null) throw new ArgumentNullException(nameof(create));
            if (inspect == null) throw new ArgumentNullException(nameof(inspect));
            if (kind != MaterialChangeKind.Add && kind != MaterialChangeKind.Remove) throw new ArgumentOutOfRangeException(nameof(kind));
            bool attempted = false; SolidMaterialSnapshot before = null, after = null;
            try
            {
                before = measure() ?? throw new InvalidOperationException("Native material measurement is unavailable.");
                attempted = true;
                var feature = create() ?? throw new InvalidOperationException("SOLIDWORKS returned no feature.");
                inspect(feature);
                after = measure() ?? throw new InvalidOperationException("Native material measurement is unavailable.");
                var evidence = MaterialChangeVerifier.Verify(before, after, kind);
                // Native feature-name/property reads and result serialization can also fail after mutation.
                finalizeResult?.Invoke(feature, evidence);
                return new FeatureMaterialOutcome<T>(feature, evidence);
            }
            catch (Exception error)
            {
                throw new FeatureMaterialVerificationException(error, attempted, before, after);
            }
        }
    }
}
