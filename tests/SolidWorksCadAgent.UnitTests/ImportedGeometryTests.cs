using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ImportedGeometryTests
    {
        [TestMethod] public void VectorAlgebraMatchesUpstreamOracle()
        {
            Assert.AreEqual(Vec3.UnitZ, Vec3.UnitX.Cross(Vec3.UnitY));
            Assert.AreEqual(0.0, Vec3.UnitX.Dot(Vec3.UnitY));
            Assert.AreEqual(5.0, new Vec3(3,4,0).Length, 1e-12);
            Assert.IsTrue(new Vec3(0,0,2).IsParallelTo(-Vec3.UnitZ));
            Assert.IsFalse(new Vec3(0,1,2).IsParallelTo(Vec3.UnitZ,0.01));
        }
        [TestMethod] public void ZeroDirectionCannotMatchAnotherDirection() =>
            Assert.ThrowsException<ArgumentException>(() => Vec3.Zero.Normalized());
        [TestMethod] public void LargeFiniteVectorNormalizesWithoutOverflow() =>
            Assert.AreEqual(1.0, new Vec3(1e308,1e308,0).Normalized().Length, 1e-12);
        [DataTestMethod] [DataRow(double.NaN)] [DataRow(double.PositiveInfinity)]
        public void NonfiniteVectorFails(double value) => Assert.ThrowsException<ArgumentOutOfRangeException>(()=>new Vec3(value,0,0));
        [TestMethod] public void TransformUsesSolidWorksRowVectorConvention()
        {
            var transform=new Xform(new double[]{0,1,0,-1,0,0,0,0,1,0.01,-0.02,0.03,2,0,0,0});
            Assert.IsTrue(transform.ApplyPoint(Vec3.UnitX).DistanceTo(new Vec3(0.01,1.98,0.03))<1e-12);
            var point=new Vec3(.1,.2,.3);
            Assert.IsTrue(transform.Inverse().ApplyPoint(transform.ApplyPoint(point)).DistanceTo(point)<1e-12);
            Assert.AreEqual(new Vec3(-1,0,0), transform.ApplyVector(Vec3.UnitY));
        }
        [TestMethod] public void ReflectedOrthogonalTransformRoundTrips()
        {
            var x=new Xform(new double[]{-1,0,0,0,1,0,0,0,1,1,2,3,1,0,0,0});
            Assert.AreEqual(Vec3.UnitX,x.Inverse().ApplyPoint(x.ApplyPoint(Vec3.UnitX)));
        }
        [DataTestMethod] [DataRow(0.0)] [DataRow(-1.0)] [DataRow(double.NaN)]
        public void InvalidScaleFails(double scale)
        { var data=Xform.Identity.ToArrayData();data[12]=scale;Assert.ThrowsException<ArgumentException>(()=>new Xform(data)); }
        [TestMethod] public void ShearedOrTruncatedTransformFails()
        {
            var data=Xform.Identity.ToArrayData();data[1]=0.2;
            Assert.ThrowsException<ArgumentException>(()=>new Xform(data));
            Assert.ThrowsException<ArgumentException>(()=>new Xform(new double[13]));
        }
        [TestMethod] public void TransformDefensivelyCopiesArrays()
        {
            var data=Xform.Identity.ToArrayData();var x=new Xform(data);data[0]=0;
            var output=x.ToArrayData();output[0]=0;Assert.IsTrue(x.IsIdentity());
        }
        [DataTestMethod] [DataRow("+x",1,0,0)] [DataRow("y-",0,-1,0)] [DataRow(" Z ",0,0,1)] [DataRow("0,3,4",0,.6,.8)]
        public void AxisSyntaxMatchesUpstream(string text,double x,double y,double z) =>
            Assert.IsTrue(AxisSpec.Parse(text).DistanceTo(new Vec3(x,y,z))<1e-12);
        [DataTestMethod] [DataRow("0,0,0")] [DataRow("NaN,0,1")] [DataRow("x++")] [DataRow("garbage")]
        public void InvalidAxisFails(string text)=>Assert.ThrowsException<ArgumentException>(()=>AxisSpec.Parse(text));
        [TestMethod] public void UnorderedRectangleFormsOneClosedChain()
        {
            var edges=new[]{(new Vec3(1,1,0),new Vec3(0,1,0)),(Vec3.Zero,Vec3.UnitX),
                (Vec3.UnitY,Vec3.Zero),(new Vec3(1,1,0),Vec3.UnitX)};
            var chain=EdgeChain.Order(edges,1e-9);
            Assert.IsNotNull(chain);Assert.IsTrue(chain.Closed);Assert.AreEqual(4,chain.Corners);
            CollectionAssert.AreEquivalent(new[]{0,1,2,3},chain.Order);
        }
        [TestMethod] public void DisconnectedOrDuplicateEdgesDoNotFormAChain()
        {
            Assert.IsNull(EdgeChain.Order(new[]{(Vec3.Zero,Vec3.UnitX),(new Vec3(3,0,0),new Vec3(4,0,0))},1e-9));
            Assert.IsNull(EdgeChain.Order(new[]{(Vec3.Zero,Vec3.UnitX),(Vec3.UnitX,Vec3.Zero)},1e-9));
        }
        [TestMethod] public void BranchedEdgesDoNotFormAChain()=>Assert.IsNull(EdgeChain.Order(new[]{
            (Vec3.Zero,Vec3.UnitX),(Vec3.Zero,Vec3.UnitY),(Vec3.Zero,Vec3.UnitZ)},1e-9));
        [TestMethod] public void OpenChainIncludesReverseAndStartsAtFreeEnd()
        {
            var chain=EdgeChain.Order(new[]{(Vec3.UnitX,Vec3.Zero),(Vec3.UnitX,new Vec3(2,0,0))},1e-9);
            Assert.IsNotNull(chain);Assert.IsFalse(chain.Closed);Assert.IsTrue(chain.Reversed[0]);
            Assert.AreEqual(Vec3.Zero,chain.Start);Assert.AreEqual(0,chain.Corners);
        }
        [TestMethod] public void ChainRejectsInvalidToleranceAndExcessiveWork()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(()=>EdgeChain.Order(new[]{(Vec3.Zero,Vec3.UnitX)},double.NaN));
            Assert.ThrowsException<ArgumentOutOfRangeException>(()=>EdgeChain.Order(Enumerable.Repeat((Vec3.Zero,Vec3.UnitX),257).ToArray(),1e-9));
        }
    }
}
