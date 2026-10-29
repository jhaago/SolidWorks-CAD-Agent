using System;

namespace SolidWorksCadAgent.Core.Geometry
{
    /// <summary>COM-free inspection facts. InspectionId is not a persistent selection handle.</summary>
    public sealed class TopologyFact
    {
        public string InspectionId { get; set; }
        public string Kind { get; set; }
        public string Geometry { get; set; }
        public string BodyName { get; set; }
        public string FeatureName { get; set; }
        public string[] FeatureNames { get; set; }
        public Vec3 PositionMm { get; set; }
        public string PositionMethod { get; set; }
        public Vec3? Normal { get; set; }
        public Vec3? Direction { get; set; }
        public double? RadiusMm { get; set; }
        public double? LengthMm { get; set; }
        public double? AreaMm2 { get; set; }
        public Vec3? StartMm { get; set; }
        public Vec3? EndMm { get; set; }
    }
    public sealed class TopologyQueryException : Exception
    {
        public TopologyQueryException(string message) : base(message) { }
    }
}
