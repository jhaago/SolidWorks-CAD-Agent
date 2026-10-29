using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class TopologyQueryTests
    {
        [TestMethod] public void SignedFaceNormalDistinguishesTopFromBottom()
        {
            var found=Query("{'kind':'Face','where':{'surface':'plane','normal':'+y'},'expect':1}").Apply(new[]{Face("top",Vec3.UnitY),Face("bottom",-Vec3.UnitY)});
            Assert.AreEqual("top",found.Single().InspectionId);
        }
        [TestMethod] public void ParallelEdgesAcceptEitherOrientation()
        {
            var query=Query("{'kind':'Edge','where':{'curve':'line','parallelTo':'x'},'expect':2}");
            Assert.AreEqual(2,query.Apply(new[]{Edge("a",Vec3.UnitX),Edge("b",-Vec3.UnitX),Edge("c",Vec3.UnitY)}).Count);
        }
        [TestMethod] public void AllSpecifiedFiltersMustMatch()
        {
            var a=Edge("a",Vec3.UnitX);a.LengthMm=10;a.BodyName="BodyA";
            var b=Edge("b",Vec3.UnitX);b.LengthMm=20;b.BodyName="BodyA";
            Assert.AreEqual("a",Query("{'kind':'Edge','where':{'lengthMm':10,'body':'BodyA'},'expect':1}").Apply(new[]{a,b}).Single().InspectionId);
        }
        [TestMethod] public void RadiusFilterRequiresNativeRadiusEvidence()
        {
            var circle=Edge("c",Vec3.UnitZ);circle.Geometry="circle";circle.RadiusMm=5;
            Assert.AreEqual(1,Query("{'kind':'Edge','where':{'curve':'circle','radiusMm':5},'expect':1}").Apply(new[]{circle,Edge("line",Vec3.UnitX)}).Count);
        }
        [TestMethod] public void ExpectedCardinalityMismatchFails() =>
            Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Face','expect':1}").Apply(new[]{Face("a",Vec3.UnitY),Face("b",Vec3.UnitY)}));
        [TestMethod] public void ZeroExpectedMatchesIsExplicitlySupported()=>Assert.AreEqual(0,Query("{'kind':'Face','expect':0}").Apply(new TopologyFact[0]).Count);
        [TestMethod] public void TiedExtremeDoesNotSilentlyPickFirst()
        {
            var a=Face("a",Vec3.UnitY);var b=Face("b",Vec3.UnitY);
            Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Face','where':{'extreme':'+y'},'expect':1}").Apply(new[]{a,b}));
        }
        [TestMethod] public void SignedVectorExtremeUsesDotProduct()
        {
            var a=Face("a",Vec3.UnitY);a.PositionMm=new Vec3(5,0,0);
            var b=Face("b",Vec3.UnitY);b.PositionMm=new Vec3(0,5,0);
            Assert.AreEqual("a",Query("{'kind':'Face','where':{'extreme':'1,-1,0'},'expect':1}").Apply(new[]{a,b}).Single().InspectionId);
        }
        [TestMethod] public void ListingOrderIsIndependentOfNativeEnumeration()
        {
            var a=Face("a",Vec3.UnitY);a.PositionMm=new Vec3(0,0,0);
            var b=Face("b",Vec3.UnitY);b.PositionMm=new Vec3(1,0,0);
            CollectionAssert.AreEqual(new[]{"a","b"},Query("{'kind':'Face'}").Apply(new[]{b,a}).Select(x=>x.InspectionId).ToArray());
        }
        [DataTestMethod]
        [DataRow("{'kind':'Face','unknown':1}")]
        [DataRow("{'kind':'Face','where':{'nromal':'y'}}")]
        [DataRow("{'kind':'Face','expect':'1'}")]
        [DataRow("{'kind':'Face','expect':1.5}")]
        [DataRow("{'kind':'Face','expect':9223372036854775808}")]
        [DataRow("{'kind':'Face','where':{'curve':'line'}}")]
        [DataRow("{'kind':'Edge','where':{'normal':'y'}}")]
        [DataRow("{'kind':'Edge','where':{'radiusMm':-1}}")]
        [DataRow("{'kind':'Edge','where':{'angleTolDeg':90}}")]
        [DataRow("{'kind':'Edge','where':{'parallelTo':'0,0,0'}}")]
        public void MalformedOrWrongKindFiltersFailBeforeInspection(string json)=>Assert.ThrowsException<ArgumentException>(()=>Query(json));
        [TestMethod] public void DuplicateOrNonfiniteNativeFactsFailClosed()
        {
            var a=Edge("a",Vec3.UnitX);a.LengthMm=double.NaN;
            Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Edge'}").Apply(new[]{a}));
            var b=Face("same",Vec3.UnitY);
            Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Face'}").Apply(new[]{b,b}));
        }
        [TestMethod] public void MissingDirectionCannotMatchParallelFilter()=>Assert.AreEqual(0,Query("{'kind':'Edge','where':{'parallelTo':'x'},'expect':0}").Apply(new[]{new TopologyFact{InspectionId="a",Kind="Edge",Geometry="bspline"}}).Count);
        [TestMethod] public void TooManyMatchesFailInsteadOfTruncating()=>Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Face'}").Apply(Enumerable.Range(0,257).Select(i=>Face(i.ToString(),Vec3.UnitY)).ToArray()));
        [TestMethod] public void ChainOptionOrdersMeasuredStraightEdges()
        {
            var a=Edge("a",Vec3.UnitX);a.StartMm=Vec3.UnitX;a.EndMm=Vec3.Zero;
            var b=Edge("b",Vec3.UnitX);b.StartMm=Vec3.UnitX;b.EndMm=new Vec3(2,0,0);
            var query=Query("{'kind':'Edge','orderChain':true,'expect':2}");
            Assert.IsTrue(query.OrderAsChain);
            var chain=TopologyQuery.OrderStraightEdges(query.Apply(new[]{a,b}));
            Assert.IsFalse(chain.Closed);Assert.AreEqual(2,chain.Order.Length);
        }
        [TestMethod] public void ChainOptionRejectsUnmeasuredOrNonlinearEdges()=>Assert.ThrowsException<TopologyQueryException>(()=>TopologyQuery.OrderStraightEdges(new[]{Edge("a",Vec3.UnitX)}));
        [TestMethod] public void MissingOwnershipEvidenceCannotProveZeroFeatureMatches()=>
            Assert.ThrowsException<TopologyQueryException>(()=>Query("{'kind':'Edge','where':{'feature':'Boss'},'expect':0}").Apply(new[]{Edge("a",Vec3.UnitX)}));
        [TestMethod] public void EdgeFeatureFilterAcceptsEitherAdjacentFaceOwner()
        {
            var edge=Edge("a",Vec3.UnitX);edge.FeatureNames=new[]{"Boss","Cut"};
            Assert.AreEqual(1,Query("{'kind':'Edge','where':{'feature':'Cut'},'expect':1}").Apply(new[]{edge}).Count);
        }
        private static TopologyQuery Query(string json)=>TopologyQuery.Parse(JObject.Parse(json));
        private static TopologyFact Face(string id,Vec3 normal)=>new TopologyFact{InspectionId=id,Kind="Face",Geometry="plane",Normal=normal,BodyName="Body",PositionMm=Vec3.Zero};
        private static TopologyFact Edge(string id,Vec3 direction)=>new TopologyFact{InspectionId=id,Kind="Edge",Geometry="line",Direction=direction,LengthMm=1,BodyName="Body",PositionMm=Vec3.Zero};
    }
}
