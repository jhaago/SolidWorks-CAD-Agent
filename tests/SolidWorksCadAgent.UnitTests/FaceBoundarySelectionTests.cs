using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FaceBoundarySelectionTests
    {
        private static PlanarFaceFact Face(string id,Vec3 normal,Vec3 point)=>new PlanarFaceFact{Key=id,Normal=normal,PointMm=point};
        private static FaceBoundaryLoop Loop(string key,bool outer,params string[] edges)=>new FaceBoundaryLoop{Key=key,IsOuter=outer,EdgeKeys=edges};
        [TestMethod] public void PartialNativeFaceArrayCannotProveUniqueExterior()
        {
            object[] partial={Face("only-observed",Vec3.UnitY,new Vec3(0,10,0))};
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(partial,2,20000,"faces"));
            Assert.AreSame(partial,TopologyArrayEvidence.RequireComplete(partial,1,20000,"faces"));
        }
        [TestMethod] public void InvalidNativeCountAndBudgetEvidenceFailsClosed()
        {
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(null,1,20000,"faces"));
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(new object[0],0,20000,"faces"));
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(new object[2],1,20000,"faces"));
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(new object[1],-1,20000,"faces"));
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(new object[20001],20001,20000,"faces"));
            Assert.ThrowsException<TopologyQueryException>(()=>TopologyArrayEvidence.RequireComplete(new object[1],1,0,"edges"));
        }
        [TestMethod] public void NativeFacePointRequiresExactlyFiveFiniteValuesAndConvertsOnlyXyz()
        {
            var fact=PlanarFaceFact.FromNative("top",new[]{0.0,1,0},new[]{.01,.02,.03,123.0,-456});
            Assert.AreEqual(new Vec3(10,20,30),fact.PointMm);Assert.AreEqual(Vec3.UnitY,fact.Normal);
            foreach(var raw in new[]{new double[3],new double[4],new double[6],new[]{0.0,0,0,double.NaN,0},new[]{double.MaxValue,0,0,0,0}})
                Assert.ThrowsException<TopologyQueryException>(()=>PlanarFaceFact.FromNative("x",new[]{0.0,1,0},raw));
            Assert.ThrowsException<TopologyQueryException>(()=>PlanarFaceFact.FromNative("x",new double[2],new double[5]));
        }
        [DataTestMethod][DataRow("X","Max")][DataRow("X","Min")][DataRow("Y","Max")][DataRow("Y","Min")][DataRow("Z","Max")][DataRow("Z","Min")]
        public void AllSixSidesUseExactCoordinateAndOutwardNormal(string axis,string side)
        {
            var u=axis=="X"?Vec3.UnitX:axis=="Y"?Vec3.UnitY:Vec3.UnitZ;
            var expected=Face("target",side=="Max"?u:-u,u*(side=="Max"?10:-5));
            var opposite=Face("opposite",side=="Max"?-u:u,u*(side=="Max"?-5:10));
            Assert.AreSame(expected,AxisAlignedFaceSelection.Choose(new[]{opposite,expected},axis,side,-5,10));
        }
        [TestMethod] public void PocketFloorCannotReplaceMissingExteriorFace()
        {
            var floor=Face("floor",Vec3.UnitY,new Vec3(0,7,0));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{floor},"Y","Max",0,10));
            var exterior=Face("exterior",Vec3.UnitY,new Vec3(0,10,0));
            Assert.AreSame(exterior,AxisAlignedFaceSelection.Choose(new[]{floor,exterior},"Y","Max",0,10));
        }
        [TestMethod] public void SplitCoplanarExteriorIsAmbiguousRegardlessOfOrder()
        {
            var a=Face("a",Vec3.UnitY,new Vec3(0,10,0));var b=Face("b",Vec3.UnitY,new Vec3(10,10,0));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a,b},"Y","Max",0,10));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{b,a},"Y","Max",0,10));
        }
        [TestMethod] public void InwardAndTiltedNormalsNeverMatchExtremeSide()
        {
            var inward=Face("inward",-Vec3.UnitY,new Vec3(0,10,0));
            var tilted=Face("tilted",new Vec3(.001,1,0).Normalized(),new Vec3(0,10,0));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{inward,tilted},"Y","Max",0,10));
        }
        [TestMethod] public void TinyNativeCoordinateNoiseIsAcceptedButNotOffsetFaces()
        {
            var a=Face("a",Vec3.UnitY,new Vec3(0,10.000001,0));
            Assert.AreSame(a,AxisAlignedFaceSelection.Choose(new[]{a},"Y","Max",0,10));
            a.PointMm=new Vec3(0,9.999,0);
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a},"Y","Max",0,10));
        }
        [TestMethod] public void MalformedOrDuplicateFaceEvidenceFailsClosed()
        {
            var a=Face("a",Vec3.UnitY,new Vec3(0,10,0));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a,a},"Y","Max",0,10));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new PlanarFaceFact[]{null,a},"Y","Max",0,10));
            a.Normal=Vec3.Zero;Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a},"Y","Max",0,10));
            a.Normal=Vec3.UnitY*2;Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a},"Y","Max",0,10));
            a.Normal=Vec3.UnitY;a.Key=" ";Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new[]{a},"Y","Max",0,10));
        }
        [DataTestMethod][DataRow("x","Max",0.0,10.0)][DataRow("Y","max",0.0,10.0)][DataRow("Y","Max",10.0,0.0)]
        [DataRow("Y","Max",0.0,0.0)][DataRow("Y","Max",double.NaN,10.0)][DataRow("Y","Max",0.0,double.PositiveInfinity)]
        public void InvalidAxisSideOrBodyBoundsAreRejected(string axis,string side,double min,double max)=>
            Assert.ThrowsException<ArgumentException>(()=>AxisAlignedFaceSelection.Choose(new PlanarFaceFact[0],axis,side,min,max));
        [TestMethod] public void FaceBudgetsAndMissingFacesFail()
        {
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(null,"Y","Max",0,10));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new PlanarFaceFact[0],"Y","Max",0,10));
            Assert.ThrowsException<TopologyQueryException>(()=>AxisAlignedFaceSelection.Choose(new PlanarFaceFact[20001],"Y","Max",0,10));
        }
        [TestMethod] public void OuterLoopIsFoundAfterHoleAndOnlyOuterEdgesAreReturned()
        {
            var hole=Loop("hole",false,"circle");var outer=Loop("outer",true,"a","b","c","d");
            var result=FaceBoundaryLoops.Outer(new[]{hole,outer});
            Assert.AreSame(outer,result);CollectionAssert.AreEqual(new[]{"a","b","c","d"},result.EdgeKeys.ToArray());
        }
        [TestMethod] public void MissingOrMultipleOuterLoopsAreRejected()
        {
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("hole",false,"a")}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("a",true,"a"),Loop("b",true,"b")}));
        }
        [TestMethod] public void SingularEmptyDuplicateAndIncompleteLoopsAreRejected()
        {
            var a=Loop("a",true,"a");a.IsSingular=true;
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{a}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("a",true)}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("a",true,"a","a")}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("a",true,"a"),Loop("b",false,"a")}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("same",true,"a"),Loop("same",false,"b")}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new FaceBoundaryLoop[]{null}));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{new FaceBoundaryLoop{Key="a",IsOuter=true}}));
        }
        [TestMethod] public void LoopAndEdgeBudgetsFailBeforeTruncation()
        {
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new FaceBoundaryLoop[4097]));
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(new[]{Loop("a",true,Enumerable.Range(0,257).Select(i=>i.ToString()).ToArray())}));
            var many=Enumerable.Range(0,4001).Select(i=>Loop(i.ToString(),i==0,Enumerable.Range(0,5).Select(j=>i+":"+j).ToArray())).ToArray();
            Assert.ThrowsException<TopologyQueryException>(()=>FaceBoundaryLoops.Outer(many));
        }
        [TestMethod] public void BoundaryBudgetSupportsClosedCircleOr256DistinctEdges()
        {
            Assert.AreEqual(1,FaceBoundaryLoops.Outer(new[]{Loop("circle",true,"edge")}).EdgeKeys.Count);
            Assert.AreEqual(256,FaceBoundaryLoops.Outer(new[]{Loop("polygon",true,Enumerable.Range(0,256).Select(i=>i.ToString()).ToArray())}).EdgeKeys.Count);
        }
    }
}
