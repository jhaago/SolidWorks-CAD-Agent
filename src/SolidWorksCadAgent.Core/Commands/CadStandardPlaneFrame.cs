using System;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>A point expressed in the SOLIDWORKS part-model coordinate system, in millimetres.</summary>
    public sealed class CadModelPointMm
    {
        internal CadModelPointMm(double x, double y, double z) { Xmm = x; Ymm = y; Zmm = z; }
        public double Xmm { get; }
        public double Ymm { get; }
        public double Zmm { get; }
    }

    /// <summary>Axis-aligned part-model bounds in millimetres.</summary>
    public sealed class CadModelBoundsMm
    {
        internal CadModelBoundsMm(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            MinXmm = minX; MaxXmm = maxX; MinYmm = minY; MaxYmm = maxY; MinZmm = minZ; MaxZmm = maxZ;
        }
        public double MinXmm { get; }
        public double MaxXmm { get; }
        public double MinYmm { get; }
        public double MaxYmm { get; }
        public double MinZmm { get; }
        public double MaxZmm { get; }
    }

    /// <summary>
    /// Maps sketch-local X/Y and positive feature-normal depth to model coordinates for the three
    /// SOLIDWORKS origin planes. The handedness matches the standard native template planes.
    /// </summary>
    public sealed class CadStandardPlaneFrame
    {
        private CadStandardPlaneFrame(string planeName,
            double xX, double xY, double xZ, double yX, double yY, double yZ,
            double nX, double nY, double nZ)
        {
            PlaneName = planeName;
            _transform = new Xform(new[]{xX,xY,xZ,yX,yY,yZ,nX,nY,nZ,0,0,0,1,0,0,0});
        }

        private readonly Xform _transform;
        public string PlaneName { get; }

        public static CadStandardPlaneFrame FromPlaneName(string planeName)
        {
            switch (planeName)
            {
                case "Top Plane": return new CadStandardPlaneFrame(planeName,
                    1, 0, 0, 0, 0, -1, 0, 1, 0);
                case "Front Plane": return new CadStandardPlaneFrame(planeName,
                    1, 0, 0, 0, 1, 0, 0, 0, 1);
                case "Right Plane": return new CadStandardPlaneFrame(planeName,
                    0, 0, -1, 0, 1, 0, 1, 0, 0);
                default: throw new ArgumentException("Only the Top Plane, Front Plane and Right Plane have a supported V2 coordinate frame.", nameof(planeName));
            }
        }

        public CadModelPointMm ToModel(double sketchXmm, double sketchYmm, double normalDepthMm)
        {
            RequireFinite(sketchXmm, nameof(sketchXmm));
            RequireFinite(sketchYmm, nameof(sketchYmm));
            RequireFinite(normalDepthMm, nameof(normalDepthMm));
            var point = _transform.ApplyPoint(new Vec3(sketchXmm,sketchYmm,normalDepthMm));
            return new CadModelPointMm(point.X,point.Y,point.Z);
        }

        public CadModelBoundsMm TransformBounds(double minSketchXmm, double maxSketchXmm,
            double minSketchYmm, double maxSketchYmm, double minNormalDepthMm, double maxNormalDepthMm)
        {
            if (minSketchXmm > maxSketchXmm || minSketchYmm > maxSketchYmm || minNormalDepthMm > maxNormalDepthMm)
                throw new ArgumentException("Coordinate frame bounds must be ordered.");
            var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
            var minY = double.PositiveInfinity; var maxY = double.NegativeInfinity;
            var minZ = double.PositiveInfinity; var maxZ = double.NegativeInfinity;
            foreach (var x in new[] { minSketchXmm, maxSketchXmm })
                foreach (var y in new[] { minSketchYmm, maxSketchYmm })
                    foreach (var depth in new[] { minNormalDepthMm, maxNormalDepthMm })
                    {
                        var point = ToModel(x, y, depth);
                        minX = Math.Min(minX, point.Xmm); maxX = Math.Max(maxX, point.Xmm);
                        minY = Math.Min(minY, point.Ymm); maxY = Math.Max(maxY, point.Ymm);
                        minZ = Math.Min(minZ, point.Zmm); maxZ = Math.Max(maxZ, point.Zmm);
                    }
            return new CadModelBoundsMm(minX, maxX, minY, maxY, minZ, maxZ);
        }

        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Coordinates must be finite.");
        }
    }
}
