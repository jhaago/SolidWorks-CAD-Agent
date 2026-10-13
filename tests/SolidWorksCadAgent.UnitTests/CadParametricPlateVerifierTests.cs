using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.AgentHost.Jobs;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class CadParametricPlateVerifierTests
    {
        [TestMethod] public void AllThreeDrivingParametersAndNativeConstraintsAreRequired()
        {
            var intent=new CadParametricPlateIntent{WidthMm=120,LengthMm=60,ThicknessMm=10};var observed=Valid();
            Assert.AreEqual(0,CadParametricPlateVerifier.Check(intent,observed).Count);
            observed.Dimensions.RemoveAt(2);Assert.IsTrue(CadParametricPlateVerifier.Check(intent,observed).Count>0);
        }
        [TestMethod] public void DrivenDuplicateWrongOwnerAndWrongValueFail()
        {
            var intent=new CadParametricPlateIntent{WidthMm=120,LengthMm=60,ThicknessMm=10};
            var o=Valid();o.Dimensions[0].IsDriving=false;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.Dimensions.Add(o.Dimensions[0]);Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.Dimensions[0].OwnerEntityId=Guid.NewGuid();Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.Dimensions[0].ValueMm=60;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.Dimensions[0].ValueMm=double.NaN;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
        }
        [TestMethod] public void UnderdefinedFixedConstructionContoursAndRebuildErrorsFail()
        {
            var intent=new CadParametricPlateIntent{WidthMm=120,LengthMm=60,ThicknessMm=10};
            var o=Valid();o.FullyDefined=false;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.HasFixedContourEntities=true;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.RequiredRelationsPresent=false;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.ContourEdgeCount=5;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
            o=Valid();o.RebuildErrorCount=1;Assert.IsTrue(CadParametricPlateVerifier.Check(intent,o).Count>0);
        }
        private static CadParametricPlateObservation Valid()
        {
            var model=Guid.NewGuid();var sketch=Guid.NewGuid();
            var o=new CadParametricPlateObservation{EvidenceVersion=1,ModelId=model,SketchEntityId=sketch,ConfigurationKey="Default",FullyDefined=true,RequiredRelationsPresent=true,ContourEdgeCount=4,ConstructionEdgeCount=1,SolidBodyCount=1};
            foreach(var role in new[]{"width","length","thickness"})o.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(model,sketch,role),Role=role,OwnerEntityId=role=="thickness"?CadParametricIdentity.For(model,sketch,"boss"):sketch,IsDriving=true,ValueMm=role=="width"?120:role=="length"?60:10});
            return o;
        }
    }
}
