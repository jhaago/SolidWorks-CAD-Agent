using System;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    public enum MaterialChangeKind { Add, Remove }

    public sealed class MaterialChangeEvidence
    {
        internal MaterialChangeEvidence(SolidMaterialSnapshot before, SolidMaterialSnapshot after, double tolerance, string material)
        { Before = before; After = after; ToleranceMm3 = tolerance; Material = material; }
        public int EvidenceVersion => 1;
        public SolidMaterialSnapshot Before { get; }
        public SolidMaterialSnapshot After { get; }
        public double VolumeDeltaMm3 => After.VolumeMm3 - Before.VolumeMm3;
        public int BodyCountDelta => After.BodyCount - Before.BodyCount;
        public double ToleranceMm3 { get; }
        public string Material { get; }
    }

    public static class MaterialChangeVerifier
    {
        public static MaterialChangeEvidence Verify(SolidMaterialSnapshot before, SolidMaterialSnapshot after, MaterialChangeKind kind)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (kind != MaterialChangeKind.Add && kind != MaterialChangeKind.Remove)
                throw new ArgumentOutOfRangeException(nameof(kind));
            // Conservative detection floor, not an engineering dimensional tolerance.
            double tolerance = Math.Max(1e-6, Math.Max(before.VolumeMm3, after.VolumeMm3) * 1e-12);
            double delta = after.VolumeMm3 - before.VolumeMm3;
            if (kind == MaterialChangeKind.Add ? delta <= tolerance : delta >= -tolerance)
                throw new InvalidOperationException(kind == MaterialChangeKind.Add
                    ? "Feature did not add measurable solid material. Inspect its definition and direction."
                    : "Feature did not remove measurable solid material. Inspect its definition and intersection.");
            return new MaterialChangeEvidence(before, after, tolerance, kind == MaterialChangeKind.Add ? "added" : "removed");
        }
    }
}
