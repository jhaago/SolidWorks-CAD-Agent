using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class CadParametricHoleContractTests
    {
        internal const string Prompt="V2 parametric plate with hole: 120 x 60 x 10 mm; hole diameter 8 mm at x=20 mm, y=10 mm; save as parametric/hole.sldprt";
        internal static CadPlanV2Document Hole()=>CadPlanV2HostNormalizer.Normalize(CadPlanDocumentReader.ReadCandidate(CadParametricHolePrompt.BuildCandidate(Prompt)).CandidateV2);
        [TestMethod] public void ExplicitCandidateUsesSeparateOwnedSketchAndIndependentGeometry()
        {
            var plan=Hole();Assert.AreEqual(0,CadPlanV2Preflight.Validate(plan,true).Count);
            CollectionAssert.AreEqual(new[]{1,1,2,1,3,1,2,1,2,1},plan.Steps.Select(s=>s.OperationVersion.Value).ToArray());
            Assert.AreNotEqual(plan.Steps[1].OutputEntityId,plan.Steps[5].OutputEntityId);
            Assert.AreEqual(plan.Steps[5].OutputEntityId,plan.Steps[8].Inputs.ProfileSketch.EntityId);
            var hole=CadParametricHoleContract.ReadIntent(plan);Assert.AreEqual(8.0,hole.DiameterMm);Assert.AreEqual(20.0,hole.Xmm);Assert.AreEqual(10.0,hole.Ymm);
            var plate=CadParametricPlateContract.ReadIntent(plan);Assert.AreEqual(120.0,plate.WidthMm);Assert.AreEqual(60.0,plate.LengthMm);Assert.AreEqual(10.0,plate.ThicknessMm);
            Assert.AreEqual(72000-Math.PI*16*10,CadV2PrismaticGeometry.Analyze(plan).VolumeMm3,0.000001);
            Assert.AreEqual(9,CadParametricIdentity.Roles.Length);Assert.AreEqual(4,CadParametricIdentity.HoleRoles.Count);
            Assert.AreNotEqual(CadParametricIdentity.For(Guid.NewGuid(),plan.Steps[1].OutputEntityId.Value,"width"),CadParametricIdentity.For(Guid.NewGuid(),plan.Steps[5].OutputEntityId.Value,"holeX"));
        }
        [TestMethod] public void RejectsUnsafeOffsetsSizesAndEdgeContactBeforeMutation()
        {
            foreach(var field in new[]{"centerXmm","centerYmm","diameterMm"})
            foreach(var value in new[]{0.0,-1.0,double.NaN,double.PositiveInfinity,double.Epsilon,1e308})
            {var plan=Hole();plan.Steps[6].Parameters[field]=value;Assert.IsTrue(CadParametricHoleContract.Validate(plan).Count>0,field+"="+value);}
            var tangent=Hole();tangent.Steps[6].Parameters["centerXmm"]=56;Assert.IsTrue(CadParametricHoleContract.Validate(tangent).Count>0);
            tangent=Hole();tangent.Steps[6].Parameters["centerYmm"]=26;Assert.IsTrue(CadParametricHoleContract.Validate(tangent).Count>0);
        }
        [TestMethod] public void RejectsMixedVersionsPlanesAndAdditionalOperations()
        {
            foreach(var index in new[]{2,4,6,8}){var plan=Hole();plan.Steps[index].OperationVersion=1;Assert.IsTrue(CadPlanV2Preflight.Validate(plan,true).Count>0);}
            var bad=Hole();bad.Steps[5].Parameters["plane"]="Front Plane";Assert.IsTrue(CadParametricHoleContract.Validate(bad).Count>0);
            bad=Hole();bad.Steps.Insert(9,bad.Steps[8]);Assert.IsTrue(CadParametricHoleContract.Validate(bad).Count>0);
            Assert.AreEqual(0,CadParametricPlateContract.Validate(CadParametricPlateContractTests.Plate()).Count);
        }
        [TestMethod] public void MalformedScopeAndPathsDoNotBecomeLegacyOrCloudRequests()
        {
            foreach(var text in new[]{Prompt.Replace("x=20","x=-20"),Prompt.Replace("y=10","y=0"),Prompt.Replace("diameter 8","diameter NaN"),Prompt.Replace("parametric/hole.sldprt","../hole.sldprt"),Prompt.Replace("parametric/hole.sldprt","C:/hole.sldprt"),Prompt.Replace("hole diameter","hole radius")})
                Assert.ThrowsException<ArgumentException>(()=>CadParametricHolePrompt.BuildCandidate(text),text);
            Assert.IsFalse(CadParametricHolePrompt.IsRequest("V2 parametric plate: 120 x 60 x 10 mm; save as plate.sldprt"));
            var equal=CadPlanDocumentReader.ReadCandidate(CadParametricHolePrompt.BuildCandidate(Prompt.Replace("120 x 60","120 x 120").Replace("diameter 8","diameter 20").Replace("y=10","y=20")));
            Assert.IsTrue(equal.IsValid,string.Join(" ",equal.Errors));
        }
    }
}

