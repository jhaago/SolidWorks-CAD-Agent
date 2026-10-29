using System;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.Core.Interop
{
    /// <summary>IEdge.Evaluate2(t,0): xyz plus one packed status double; lower int32 must be 1.</summary>
    public static class NativeEdgeEvaluation
    {
        public static double ParameterMidpoint(double minimum,double maximum)
        {
            if(!Vec3.Finite(minimum)||!Vec3.Finite(maximum)||minimum>=maximum)
                throw new InvalidOperationException("Native edge parameter range is invalid.");
            return minimum/2+maximum/2;
        }
        public static Vec3 ReadPositionMetres(double[] values)
        {
            if(values==null||values.Length!=4||unchecked((int)BitConverter.DoubleToInt64Bits(values[3]))!=1)
                throw new InvalidOperationException("Native edge evaluation did not report success.");
            // The status word is packed metadata, not a floating-point measurement.
            return Vec3.From(values);
        }
    }
}
