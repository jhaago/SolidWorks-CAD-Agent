using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FinishingPortTests
    {
        private static EdgeFinishingDefinition Definition(EdgeFinishingKind kind)=>new EdgeFinishingDefinition
        {Kind=kind,Variant=kind==EdgeFinishingKind.Fillet?"constant_radius":"angle_distance",SizeMm=1,EdgeCount=4,AngleDegrees=kind==EdgeFinishingKind.Chamfer?(double?)45:null};
        [TestMethod] public void NativeDefinitionUsesNamedKindsInToolJson()
        {
            var json=SolidWorksCadAgent.Contracts.Cad.CadCommandResult.Ok(Definition(EdgeFinishingKind.Fillet)).Data;
            Assert.AreEqual("fillet",json.Value<string>("kind"));Assert.AreEqual(1.0,json.Value<double>("sizeMm"));
        }
        [TestMethod] public void OnlyCreatedUnsavedPartsAreEligible()
        {
            var guard=new FreshPartMutationGuard();
            Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved(""));
            guard.BindCreated();guard.RequireUnsaved("");
            Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved("part.sldprt"));
            Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved(null));
            guard.BindExisting();Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved(""));
        }
        [TestMethod] public void UncertainFailureRequiresANewCreatedBinding()
        {
            var guard=new FreshPartMutationGuard();guard.BindCreated();guard.Quarantine();
            Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved(""));
            guard.BindExisting();Assert.ThrowsException<InvalidOperationException>(()=>guard.RequireUnsaved(""));
            guard.BindCreated();guard.RequireUnsaved("");
        }
        [DataTestMethod][DataRow(EdgeFinishingKind.Fillet)][DataRow(EdgeFinishingKind.Chamfer)]
        public void ExactDefinitionAcceptsOnlyIntendedSizeCountAndOptions(EdgeFinishingKind kind)=>
            EdgeFinishingIntent.VerifyDefinition(kind,1,4,Definition(kind));
        [TestMethod] public void IncorrectOrIncompleteDefinitionFails()
        {
            var d=Definition(EdgeFinishingKind.Fillet);d.SizeMm=2;
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            d=Definition(EdgeFinishingKind.Fillet);d.EdgeCount=3;
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            d=Definition(EdgeFinishingKind.Fillet);d.TangentPropagation=true;
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            d=Definition(EdgeFinishingKind.Fillet);d.MultipleRadius=true;
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            d=Definition(EdgeFinishingKind.Fillet);d.Asymmetric=true;
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            d=Definition(EdgeFinishingKind.Fillet);d.Variant="face_fillet";
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,Definition(EdgeFinishingKind.Chamfer)));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,null));
        }
        [DataTestMethod][DataRow(44.0)][DataRow(46.0)][DataRow(double.NaN)][DataRow(double.PositiveInfinity)]
        public void ChamferAngleMustBeVerifiedAs45Degrees(double angle)
        {var d=Definition(EdgeFinishingKind.Chamfer);d.AngleDegrees=angle;Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Chamfer,1,4,d));}
        [DataTestMethod][DataRow(0.0)][DataRow(-1.0)][DataRow(double.NaN)][DataRow(double.PositiveInfinity)]
        public void InvalidNativeSizesAreRejected(double size)
        {var d=Definition(EdgeFinishingKind.Fillet);d.SizeMm=size;Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyDefinition(EdgeFinishingKind.Fillet,1,4,d));}
        [TestMethod] public void MissingRoundGeometryCannotBecomeUnverifiedSuccess()
        {
            EdgeFinishingIntent.VerifyRoundRadii(1,new[]{1.0,1.000001});
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyRoundRadii(1,new double[0]));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyRoundRadii(1,new[]{1.0,2}));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyRoundRadii(1,new[]{double.NaN}));
        }
        private static FinishingLine Line(string id,Vec3 point,Vec3? direction=null)=>new FinishingLine{Key=id,PointMm=point,Direction=direction??Vec3.UnitX};
        [TestMethod] public void ChamferLegsAreMeasuredFromEachOriginalEdge()
        {
            var expected=new[]{Line("outer",Vec3.Zero)};
            IReadOnlyList<FinishingLine>[] faces={new[]{Line("a",new Vec3(0,1,0)),Line("b",new Vec3(0,0,1)),Line("end",Vec3.Zero,Vec3.UnitY)}};
            EdgeFinishingIntent.VerifyChamferLegs(1,expected,faces);
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyChamferLegs(2,expected,faces));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyChamferLegs(1,new[]{Line("unmatched",new Vec3(0,10,0))},faces));
        }
        [TestMethod] public void TwoSameSideOrMissingBoundaryOffsetsDoNotVerifyChamfer()
        {
            var expected=new[]{Line("outer",Vec3.Zero)};
            IReadOnlyList<FinishingLine>[] same={new[]{Line("a",new Vec3(0,1,0)),Line("b",new Vec3(0,1,0))}};
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyChamferLegs(1,expected,same));
            IReadOnlyList<FinishingLine>[] opposed={new[]{Line("a",new Vec3(0,1,0)),Line("b",new Vec3(0,-1,0))}};
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyChamferLegs(1,expected,opposed));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyChamferLegs(1,expected,new IReadOnlyList<FinishingLine>[0]));
        }
        [TestMethod] public void FinishingMustAddExactlyOneFeatureAndKeepExistingInventory()
        {
            var before=FeatureInventory.Capture(new[]{Pair("Sketch1","ProfileFeature"),Pair("Boss1","Boss")});
            var after=FeatureInventory.Capture(new[]{Pair("Sketch1","ProfileFeature"),Pair("Boss1","Boss"),Pair("AgentFillet1","Fillet")});
            EdgeFinishingIntent.VerifyInventory(before,after,"AgentFillet1","Fillet");
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyInventory(before,after,"other","Fillet"));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyInventory(before,FeatureInventory.Capture(new[]{Pair("AgentFillet1","Fillet")}),"AgentFillet1","Fillet"));
            Assert.ThrowsException<InvalidOperationException>(()=>EdgeFinishingIntent.VerifyInventory(before,after,"AgentFillet1","Chamfer"));
        }
        [TestMethod] public void MeaningfulNamesDoNotOverwriteExistingFeatureNames()
        {
            var inventory=FeatureInventory.Capture(new[]{Pair("AgentFillet1","Fillet"),Pair("agentfillet2","Fillet")});
            Assert.AreEqual("AgentFillet3",EdgeFinishingIntent.NextFeatureName(EdgeFinishingKind.Fillet,inventory));
            Assert.AreEqual("AgentChamfer1",EdgeFinishingIntent.NextFeatureName(EdgeFinishingKind.Chamfer,inventory));
        }
        private static KeyValuePair<string,string> Pair(string key,string value)=>new KeyValuePair<string,string>(key,value);
    }
}
