using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SketchConstraintGraphTests
    {
        private static JObject Graph()=>JObject.Parse("{'entities':[{'id':'base','type':'line'},{'id':'side','type':'line'},{'id':'hole','type':'circle'}],'constraints':[{'type':'horizontal','entities':['base']},{'type':'coincident','entities':['base.end','side.start']}],'dimensions':[{'id':'width','type':'length','entities':['base'],'value':100,'unit':'mm'},{'id':'diameter','type':'diameter','entities':['hole'],'value':8,'unit':'mm'}],'solveTarget':'fully_defined'}");
        [TestMethod] public void ValidGraphReturnsRelationsButDoesNotClaimASolve()
        {
            var result=SketchConstraintGraph.Analyze(Graph());Assert.IsTrue(result.Valid,string.Join(" ",result.Errors));
            Assert.IsFalse(result.NativeSolved);Assert.AreEqual("sgHORIZONTAL2D",result.RelationCodes[0]);
            Assert.AreEqual("fully_defined",result.RequestedSolveTarget);
        }
        [TestMethod] public void MissingReferencedEntityFails()
        {var g=Graph();g["constraints"][0]["entities"][0]="missing";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void DuplicateEntityIdentifiersFail()
        {var g=Graph();g["entities"][1]["id"]="base";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void DuplicateRelationsAreDetectedRegardlessOfReferenceOrder()
        {var g=Graph();((JArray)g["constraints"]).Add(JObject.Parse("{'type':'coincident','entities':['side.start','base.end']}"));Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void RelationArityAndGeometryTypesAreChecked()
        {var g=Graph();g["constraints"][0]["entities"]=new JArray("hole");Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void InvalidEndpointReferenceFails()
        {var g=Graph();g["constraints"][1]["entities"][0]="hole.start";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void FixedGeometryNeedsExplicitTraceOptIn()
        {
            var g=Graph();((JArray)g["constraints"]).Add(JObject.Parse("{'type':'fixed','entities':['base']}"));
            Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);g["allowFixed"]=true;
            Assert.IsTrue(SketchConstraintGraph.Analyze(g).Valid);
        }
        [TestMethod] public void AngularAndLengthDimensionsUseDifferentUnits()
        {var g=Graph();g["dimensions"][0]["unit"]="deg";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void DimensionsNeedDistinctNamesAndValidTargets()
        {var g=Graph();g["dimensions"][1]["id"]="width";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void NamedReferenceCheckDoesNotTreatImplicitAxesAsSelectable()
        {var g=Graph();g["constraints"][1]["entities"][0]="axis_x";Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void UnknownFieldsAndNonfiniteDimensionValuesFail()
        {
            var g=Graph();g["dimensions"][0]["value"]=double.NaN;Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);
            g=Graph();g["constraints"][0]["typo"]=true;Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);
        }
        [TestMethod] public void EmptyGraphIsNotReadyForParametricConstruction()=>Assert.IsFalse(SketchConstraintGraph.Analyze(JObject.Parse("{'entities':[]}")).Valid);
        [TestMethod] public void ExcessiveWorkIsRejectedBeforeGraphTraversal()
        {var g=Graph();g["entities"]=new JArray(Enumerable.Range(0,257).Select(i=>new JObject{{"id","e"+i},{"type","line"}}));Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void MalformedRelationObjectsProduceDiagnosticsNotExceptions()
        {var g=Graph();g["constraints"]=new JArray(4,JValue.CreateNull());Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void OriginReferenceIsAnExplicitPoint()
        {var g=Graph();g["constraints"][1]["entities"]=new JArray("base.start","origin");Assert.IsTrue(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void SameNativePointReferenceDoesNotCreateSelfRelation()
        {var g=Graph();g["constraints"][1]["entities"]=new JArray("base.start","base.start");Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
        [TestMethod] public void UnderflowingNativeDimensionIsRejected()
        {var g=Graph();g["dimensions"][0]["value"]=double.Epsilon;Assert.IsFalse(SketchConstraintGraph.Analyze(g).Valid);}
    }
}
