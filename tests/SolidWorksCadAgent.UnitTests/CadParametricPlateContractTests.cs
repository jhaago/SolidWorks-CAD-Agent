using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadParametricPlateContractTests
    {
        internal static CadPlanV2Document Plate()
        {
            var id = Guid.NewGuid();
            return new CadPlanV2Document { PlanVersion = 2, Steps = new List<CadPlanV2Step> {
                Step("new", "NewPart", 1, "{}"),
                new CadPlanV2Step { StepKey="sketch", Command="CreateSketch", OperationVersion=1,
                    Parameters=JObject.Parse("{plane:'Top Plane'}"), OutputKey="profile", OutputEntityId=id },
                Step("rectangle", "AddRectangle", 2, "{centerXmm:0,centerYmm:0,widthMm:120,heightMm:60}"),
                Step("exit", "ExitSketch", 1, "{}"),
                new CadPlanV2Step { StepKey="boss",Command="Extrude",OperationVersion=3,Parameters=JObject.Parse("{depthMm:10}"),
                    Inputs=new CadPlanV2Inputs { ProfileSketch=new CadPlanV2Reference { Kind="Sketch",OutputKey="profile",EntityId=id } } },
                Step("save", "SavePart", 1, "{path:'parametric/plate.sldprt',allowOverwrite:false}") } };
        }
        private static CadPlanV2Step Step(string key,string command,int version,string json) =>
            new CadPlanV2Step { StepKey=key,Command=command,OperationVersion=version,Parameters=JObject.Parse(json) };
        [TestMethod] public void ValidPlatePreservesVersionPairsAndExpectedGeometry()
        {
            var plan=Plate(); Assert.AreEqual(0,CadParametricPlateContract.Validate(plan).Count);
            Assert.AreEqual(0,CadPlanV2Preflight.Validate(plan,true).Count);
            var intent=CadParametricPlateContract.ReadIntent(plan);
            Assert.AreEqual(120.0,intent.WidthMm); Assert.AreEqual(60.0,intent.LengthMm); Assert.AreEqual(10.0,intent.ThicknessMm);
            var geometry=CadV2PrismaticGeometry.Analyze(plan); Assert.AreEqual(72000.0,geometry.VolumeMm3);
            Assert.AreEqual(-60.0,geometry.MinXmm); Assert.AreEqual(60.0,geometry.MaxXmm);
            Assert.AreEqual(0.0,geometry.MinYmm); Assert.AreEqual(10.0,geometry.MaxYmm);
            Assert.AreEqual(-30.0,geometry.MinZmm); Assert.AreEqual(30.0,geometry.MaxZmm);
        }
        [TestMethod] public void LegacyRectangleAndBossKeepTheirSemantics()
        {
            var plan=Plate(); plan.Steps[2].OperationVersion=1;plan.Steps[4].OperationVersion=2;
            Assert.IsFalse(CadParametricPlateContract.IsParametric(plan));
            Assert.AreEqual(0,CadPlanV2Preflight.Validate(plan,true).Count);
            Assert.AreEqual(72000.0,CadV2PrismaticGeometry.Analyze(plan).VolumeMm3);
        }
        [TestMethod] public void RejectsPartialMixedAndForeignPlanePlans()
        {
            var plan=Plate();plan.Steps[4].OperationVersion=2; Assert.IsTrue(CadParametricPlateContract.Validate(plan).Count>0);
            plan=Plate();plan.Steps[1].Parameters["plane"]="Front Plane";Assert.IsTrue(CadParametricPlateContract.Validate(plan).Count>0);
            plan=Plate();plan.Steps[2].Parameters["centerXmm"]=1;Assert.IsTrue(CadParametricPlateContract.Validate(plan).Count>0);
            plan=Plate();plan.Steps.Add(Step("rebuild","Rebuild",1,"{}"));Assert.IsTrue(CadParametricPlateContract.Validate(plan).Count>0);
        }
        [TestMethod] public void RejectsNonfiniteUnrepresentableAndOverflowingSizes()
        {
            foreach(var value in new[]{0.0,-1,double.NaN,double.PositiveInfinity,double.Epsilon,1e308})
            { var plan=Plate();plan.Steps[2].Parameters["widthMm"]=value;Assert.IsTrue(CadParametricPlateContract.Validate(plan).Count>0,value.ToString()); }
        }
        [TestMethod] public void RoleIdentityRejectsEmptyOrUnknownInputs()
        {
            var model=Guid.NewGuid();var sketch=Guid.NewGuid();
            Assert.AreEqual(CadParametricIdentity.For(model,sketch,"width"),CadParametricIdentity.For(model,sketch,"width"));
            Assert.AreNotEqual(CadParametricIdentity.For(model,sketch,"width"),CadParametricIdentity.For(model,sketch,"length"));
            Assert.AreNotEqual(CadParametricIdentity.For(model,sketch,"width"),CadParametricIdentity.For(Guid.NewGuid(),sketch,"width"));
            Assert.ThrowsException<ArgumentException>(()=>CadParametricIdentity.For(Guid.Empty,sketch,"width"));
            Assert.ThrowsException<ArgumentException>(()=>CadParametricIdentity.For(model,sketch,"Width"));
        }
    }
}
