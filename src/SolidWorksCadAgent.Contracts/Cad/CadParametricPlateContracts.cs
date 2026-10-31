using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadParametricPlateIntent
    {
        public double WidthMm { get; set; }
        public double LengthMm { get; set; }
        public double ThicknessMm { get; set; }
        public string SavePath { get; set; }
    }
    public sealed class CadParametricRoleCapture
    {
        public Guid EntityId { get; set; }
        public string Role { get; set; }
        public string OwnerKind { get; set; }
        public byte[] OwnerReferenceBytes { get; set; }
        public string NativeDimensionFullName { get; set; }
    }
    public sealed class CadParametricDimensionObservation
    {
        public Guid EntityId { get; set; }
        public string Role { get; set; }
        public Guid OwnerEntityId { get; set; }
        public double ValueMm { get; set; }
        public bool IsDriving { get; set; }
    }
    public sealed class CadParametricPlateObservation : CadParametricSketchObservation { }
    public class CadParametricSketchObservation
    {
        public int EvidenceVersion { get; set; }
        public Guid ModelId { get; set; }
        public Guid SketchEntityId { get; set; }
        public string ConfigurationKey { get; set; }
        public bool FullyDefined { get; set; }
        public bool RequiredRelationsPresent { get; set; }
        public bool HasFixedContourEntities { get; set; }
        public int ContourEdgeCount { get; set; }
        public int ConstructionEdgeCount { get; set; }
        public int SolidBodyCount { get; set; }
        public int RebuildErrorCount { get; set; }
        public List<CadParametricRoleCapture> Roles { get; set; } = new List<CadParametricRoleCapture>();
        public List<CadParametricDimensionObservation> Dimensions { get; set; } = new List<CadParametricDimensionObservation>();
        public JObject Geometry { get; set; }
    }
}
