using System;
using SolidWorksCadAgent.Core.Units;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    public sealed class PreciseBoundingBox
    {
        public double MinXmm { get; set; }
        public double MinYmm { get; set; }
        public double MinZmm { get; set; }
        public double MaxXmm { get; set; }
        public double MaxYmm { get; set; }
        public double MaxZmm { get; set; }

        public double SizeXmm => MaxXmm - MinXmm;
        public double SizeYmm => MaxYmm - MinYmm;
        public double SizeZmm => MaxZmm - MinZmm;
    }

    public sealed class PreciseBoundsAccumulator
    {
        private bool _hasBounds;
        private double _minX;
        private double _minY;
        private double _minZ;
        private double _maxX;
        private double _maxY;
        private double _maxZ;

        public void IncludeBodyExtentsMetres(
            double minX,
            double minY,
            double minZ,
            double maxX,
            double maxY,
            double maxZ)
        {
            EnsureFinite(nameof(minX), minX);
            EnsureFinite(nameof(minY), minY);
            EnsureFinite(nameof(minZ), minZ);
            EnsureFinite(nameof(maxX), maxX);
            EnsureFinite(nameof(maxY), maxY);
            EnsureFinite(nameof(maxZ), maxZ);

            if (minX > maxX || minY > maxY || minZ > maxZ)
            {
                throw new ArgumentException("Minimum body extents must not exceed maximum body extents.");
            }

            if (!_hasBounds)
            {
                _minX = minX;
                _minY = minY;
                _minZ = minZ;
                _maxX = maxX;
                _maxY = maxY;
                _maxZ = maxZ;
                _hasBounds = true;
                return;
            }

            _minX = Math.Min(_minX, minX);
            _minY = Math.Min(_minY, minY);
            _minZ = Math.Min(_minZ, minZ);
            _maxX = Math.Max(_maxX, maxX);
            _maxY = Math.Max(_maxY, maxY);
            _maxZ = Math.Max(_maxZ, maxZ);
        }

        public PreciseBoundingBox ToMillimetres()
        {
            if (!_hasBounds)
            {
                throw new InvalidOperationException("No body extents have been supplied.");
            }

            return new PreciseBoundingBox
            {
                MinXmm = UnitConverter.MetresToMillimetres(_minX),
                MinYmm = UnitConverter.MetresToMillimetres(_minY),
                MinZmm = UnitConverter.MetresToMillimetres(_minZ),
                MaxXmm = UnitConverter.MetresToMillimetres(_maxX),
                MaxYmm = UnitConverter.MetresToMillimetres(_maxY),
                MaxZmm = UnitConverter.MetresToMillimetres(_maxZ)
            };
        }

        private static void EnsureFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "Body extents must be finite values.");
            }
        }
    }
}
