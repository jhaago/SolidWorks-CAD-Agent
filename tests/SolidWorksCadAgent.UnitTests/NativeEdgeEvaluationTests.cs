using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Geometry;
using SolidWorksCadAgent.Core.Interop;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class NativeEdgeEvaluationTests
    {
        [TestMethod] public void PackedSuccessStatusIsRequiredForThePoint() =>
            Assert.AreEqual(new Vec3(1,2,3),NativeEdgeEvaluation.ReadPositionMetres(new[]{1.0,2.0,3.0,BitConverter.Int64BitsToDouble(1)}));
        [TestMethod] public void FiniteCoordinatesDoNotOverrideNativeEvaluationFailure()=>
            Assert.ThrowsException<InvalidOperationException>(()=>NativeEdgeEvaluation.ReadPositionMetres(new[]{1.0,2.0,3.0,0.0}));
        [TestMethod] public void MissingPackedStatusIsNotSuccessfulEvaluation()=>
            Assert.ThrowsException<InvalidOperationException>(()=>NativeEdgeEvaluation.ReadPositionMetres(new[]{1.0,2.0,3.0}));
        [TestMethod] public void PackedMetadataIsNotInterpretedAsFloatingPointMeasurement()=>
            Assert.AreEqual(Vec3.Zero,NativeEdgeEvaluation.ReadPositionMetres(new[]{0.0,0.0,0.0,BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000001))}));
        [TestMethod] public void ParameterMidpointAvoidsOverflow()=>Assert.AreEqual(1.5e308,NativeEdgeEvaluation.ParameterMidpoint(1.3e308,1.7e308),1e294);
        [TestMethod] public void InvalidNativeParameterRangeIsRejected()=>Assert.ThrowsException<InvalidOperationException>(()=>NativeEdgeEvaluation.ParameterMidpoint(2,1));
        [TestMethod] public void NonfiniteNativeParameterRangeIsRejected()=>Assert.ThrowsException<InvalidOperationException>(()=>NativeEdgeEvaluation.ParameterMidpoint(double.NaN,1));
    }
}
