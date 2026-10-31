using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.AgentHost.Jobs;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass] public class CadParametricHoleVerifierTests
    {
        private static readonly CadParametricHoleIntent Intent=new CadParametricHoleIntent{DiameterMm=8,Xmm=20,Ymm=10};
        [TestMethod] public void ThreeDrivingParametersAndActualCircleGeometryAreRequired()
        {
            var o=Valid();Assert.AreEqual(0,CadParametricHoleVerifier.Check(Intent,o).Count);
            o.Dimensions.RemoveAt(2);Assert.IsTrue(CadParametricHoleVerifier.Check(Intent,o).Count>0);
            o=Valid();o.Geometry["centerXmm"]=21;Assert.IsTrue(CadParametricHoleVerifier.Check(Intent,o).Count>0);
            o=Valid();o.Geometry=null;Assert.IsTrue(CadParametricHoleVerifier.Check(Intent,o).Count>0);
        }
        [TestMethod] public void DrivenDuplicateWrongOwnerAndNonfiniteValuesFail()
        {
            Action<CadParametricHoleObservation>[] corrupt={o=>o.Dimensions[0].IsDriving=false,o=>o.Dimensions.Add(o.Dimensions[0]),
                o=>o.Dimensions[1].OwnerEntityId=Guid.NewGuid(),o=>o.Dimensions[1].EntityId=Guid.NewGuid(),o=>o.Dimensions[0].ValueMm=double.NaN,
                o=>o.Geometry["diameterMm"]=double.PositiveInfinity,o=>o.Dimensions[1].ValueMm=10};
            foreach(var change in corrupt){var o=Valid();change(o);Assert.IsTrue(CadParametricHoleVerifier.Check(Intent,o).Count>0);}
        }
        [TestMethod] public void ConstraintsContourBodyAndRebuildMustRemainValid()
        {
            Action<CadParametricHoleObservation>[] corrupt={o=>o.FullyDefined=false,o=>o.RequiredRelationsPresent=false,o=>o.HasFixedContourEntities=true,
                o=>o.ContourEdgeCount=2,o=>o.ConstructionEdgeCount=1,o=>o.SolidBodyCount=2,o=>o.RebuildErrorCount=1,o=>o.ModelId=Guid.Empty};
            foreach(var change in corrupt){var o=Valid();change(o);Assert.IsTrue(CadParametricHoleVerifier.Check(Intent,o).Count>0);}
        }
        internal static CadParametricHoleObservation Valid()
        {
            var model=Guid.NewGuid();var sketch=Guid.NewGuid();var o=new CadParametricHoleObservation{EvidenceVersion=1,ModelId=model,SketchEntityId=sketch,
                ConfigurationKey="Default",FullyDefined=true,RequiredRelationsPresent=true,ContourEdgeCount=1,SolidBodyCount=1,
                Geometry=JObject.FromObject(new{centerXmm=20,centerYmm=10,diameterMm=8})};
            foreach(var role in new[]{"holeDiameter","holeX","holeY"})o.Dimensions.Add(new CadParametricDimensionObservation{Role=role,
                EntityId=CadParametricIdentity.For(model,sketch,role),OwnerEntityId=sketch,IsDriving=true,ValueMm=role=="holeDiameter"?8:role=="holeX"?20:10});
            return o;
        }
    }
}
