using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FamilyPlanningTests
    {
        private static JObject Linear() => JObject.Parse("{'pattern':'linear','seeds':[{'id':'bolt','originMm':[10,20,30]}],'direction':'x','spacingMm':25,'count':3}");
        private static JObject Circular() => JObject.Parse("{'pattern':'circular','seeds':[{'id':'bolt','originMm':[20,0,0]}],'axis':'z','axisPointMm':[10,0,0],'angleDegrees':360,'count':4,'equalSpacing':true}");
        private static JObject Drawing() => JObject.Parse("{'sheetWidthMm':420,'sheetHeightMm':297,'modelSizeMm':[100,60,20],'views':['front','top','right','iso'],'projection':'thirdAngle'}");

        [TestMethod] public void LinearPatternExcludesSeedAndConvertsMillimetresToNativeMetres()
        {
            var result=ComponentPatternPlan.Create(Linear());
            Assert.AreEqual(3,result.Value<int>("totalInstanceCount"));
            var instances=(JArray)result["placements"];Assert.AreEqual(2,instances.Count);
            CollectionAssert.AreEqual(new[]{35.0,20,30},instances[0]["originMm"].Values<double>().ToArray());
            Assert.AreEqual("bolt",instances[0].Value<string>("seedId"));
            var native=instances[0]["solidWorksArrayData"].Values<double>().ToArray();
            Assert.AreEqual(16,native.Length);Assert.AreEqual(.035,native[9],1e-12);Assert.AreEqual(.02,native[10],1e-12);
            Assert.IsFalse(result.Value<bool>("nativeCreated"));Assert.IsTrue(result.Value<bool>("planningOnly"));
        }
        [TestMethod] public void LinearGridNormalizesBothDirectionsAndPreservesSeedRotation()
        {
            var input=Linear();input["direction"]="2,0,0";input["count"]=2;input["secondDirection"]="y";
            input["secondSpacingMm"]=10;input["secondCount"]=2;
            input["seeds"][0]["rotation"]=new JArray(0,-1,0,1,0,0,0,0,1);
            var result=ComponentPatternPlan.Create(input);var placements=(JArray)result["placements"];
            Assert.AreEqual(3,placements.Count);
            CollectionAssert.AreEqual(new[]{35.0,30,30},placements[2]["originMm"].Values<double>().ToArray());
            CollectionAssert.AreEqual(input["seeds"][0]["rotation"].Values<double>().ToArray(),placements[2]["rotation"].Values<double>().ToArray());
        }
        [TestMethod] public void CircularPatternRotatesOriginAndOrientationAroundOffsetAxis()
        {
            var input=Circular();input["seeds"][0]["rotation"]=new JArray(0,-1,0,1,0,0,0,0,1);
            var result=ComponentPatternPlan.Create(input);var placements=(JArray)result["placements"];
            Assert.AreEqual(3,placements.Count);Assert.AreEqual(90.0,result.Value<double>("stepDegrees"),1e-10);
            Assert.AreEqual(10,placements[0]["originMm"][0].Value<double>(),1e-10);
            Assert.AreEqual(10,placements[0]["originMm"][1].Value<double>(),1e-10);
            // 90-degree pattern composed with 90-degree seed rotates local +X to -X.
            var native=new Xform(placements[0]["solidWorksArrayData"].Values<double>().ToArray());
            var point=native.ApplyPoint(new Vec3(.001,0,0));
            Assert.AreEqual(.009,point.X,1e-10);Assert.AreEqual(.01,point.Y,1e-10);
            Assert.AreEqual(-1,placements[0]["rotation"][0].Value<double>(),1e-10);
        }
        [TestMethod] public void PartialCircleIncludesEndpointAndExplicitStepUsesItsAngle()
        {
            var input=Circular();input["angleDegrees"]=180;input["count"]=3;
            var result=ComponentPatternPlan.Create(input);Assert.AreEqual(90,result.Value<double>("stepDegrees"),1e-10);
            Assert.AreEqual(0,result["placements"][1]["originMm"][0].Value<double>(),1e-10);
            input["angleDegrees"]=45;input["equalSpacing"]=false;
            Assert.AreEqual(45,ComponentPatternPlan.Create(input).Value<double>("stepDegrees"),1e-10);
        }
        [TestMethod] public void MultipleSeedsRetainStableSeedAndGridOrder()
        {
            var input=Linear();((JArray)input["seeds"]).Add(JObject.Parse("{'id':'nut','originMm':[0,0,0]}"));
            var result=ComponentPatternPlan.Create(input);
            Assert.AreEqual(6,result.Value<int>("totalInstanceCount"));Assert.AreEqual(4,((JArray)result["placements"]).Count);
            Assert.AreEqual("nut",result["placements"][2].Value<string>("seedId"));
        }
        [DataTestMethod]
        [DataRow("count", "1")][DataRow("count", "2.5")][DataRow("count", "257")][DataRow("count", "9223372036854775808")]
        [DataRow("spacingMm", "0")][DataRow("spacingMm", "1e308")][DataRow("spacingMm", "'20'")]
        [DataRow("direction", "'0,0,0'")][DataRow("pattern", "'mirror'")][DataRow("unknown", "true")]
        public void InvalidPatternInputsAreRejected(string key,string json)
        {var input=Linear();input[key]=JToken.Parse(json);Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));}
        [DataTestMethod][DataRow(double.NaN)][DataRow(double.PositiveInfinity)][DataRow(double.NegativeInfinity)]
        public void NonfiniteValuesAreRejectedWithoutDependingOnJsonParserRuntime(double value)
        {
            var pattern=Linear();pattern["spacingMm"]=value;
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(pattern));
            var drawing=Drawing();drawing["modelSizeMm"]=new JArray(1,1,value);
            Assert.ThrowsException<ArgumentException>(()=>StandardDrawingLayout.Create(drawing));
        }
        [DataTestMethod]
        [DataRow("[-1,0,0,0,1,0,0,0,1]")][DataRow("[1,1,0,0,1,0,0,0,1]")][DataRow("[2,0,0,0,2,0,0,0,2]")][DataRow("[1,0,0]")]
        public void NonRigidOrReflectedSeedRotationsAreRejected(string rotation)
        {var input=Linear();input["seeds"][0]["rotation"]=JArray.Parse(rotation);Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));}
        [TestMethod] public void InvalidPatternCombinationsAreRejectedBeforeAllocation()
        {
            var input=Linear();input["secondDirection"]="-x";input["secondSpacingMm"]=10;input["secondCount"]=2;
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));
            input=Linear();((JArray)input["seeds"]).Add(input["seeds"][0].DeepClone());
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));
            input=Linear();input["count"]=200;((JArray)input["seeds"]).Add(JObject.Parse("{'id':'nut','originMm':[0,0,0]}"));
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));
            input=Circular();input["equalSpacing"]=false;input["angleDegrees"]=120;
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));
            input=Circular();input["spacingMm"]=1;
            Assert.ThrowsException<ArgumentException>(()=>ComponentPatternPlan.Create(input));
        }
        [TestMethod] public void PatternBudgetBoundaryIsSupported()
        {var input=Linear();input["count"]=256;Assert.AreEqual(255,((JArray)ComponentPatternPlan.Create(input)["placements"]).Count);}

        [TestMethod] public void ThirdAngleLayoutPreservesRequestedOrderAndLeavesSpaceForTitleBlock()
        {
            var input=Drawing();input["views"]=new JArray("iso","front","top","right");var result=StandardDrawingLayout.Create(input);
            Assert.IsTrue(result.Value<bool>("fits"));Assert.AreEqual(1,result.Value<double>("scale"),1e-12);
            Assert.IsTrue(result.Value<bool>("planningOnly"));Assert.IsFalse(result.Value<bool>("nativeCreated"));
            var slots=(JArray)result["views"];Assert.AreEqual("iso",slots[0].Value<string>("view"));
            var front=Slot(result,"front");var top=Slot(result,"top");var right=Slot(result,"right");
            Assert.IsTrue(top.Value<double>("centerYmm")>front.Value<double>("centerYmm"));
            Assert.IsTrue(right.Value<double>("centerXmm")>front.Value<double>("centerXmm"));
            Assert.AreEqual(front.Value<double>("centerXmm"),top.Value<double>("centerXmm"),1e-10);
            Assert.AreEqual(front.Value<double>("centerYmm"),right.Value<double>("centerYmm"),1e-10);
            var usable=result["usableMm"].Values<double>().ToArray();Assert.AreEqual(20,usable[0]);Assert.IsTrue(usable[1]>=50);
            foreach(JObject view in slots){var b=view["outlineMm"].Values<double>().ToArray();Assert.IsTrue(b[0]>=usable[0]&&b[1]>=usable[1]&&b[2]<=usable[2]&&b[3]<=usable[3]);}
            Assert.AreEqual((100.0+20)/Math.Sqrt(2),Slot(result,"iso").Value<double>("widthMm"),1e-10);
        }
        [TestMethod] public void FirstAngleReversesProjectedSides()
        {
            var input=Drawing();input["projection"]="firstAngle";var result=StandardDrawingLayout.Create(input);
            Assert.IsTrue(Slot(result,"top").Value<double>("centerYmm")<Slot(result,"front").Value<double>("centerYmm"));
            Assert.IsTrue(Slot(result,"right").Value<double>("centerXmm")<Slot(result,"front").Value<double>("centerXmm"));
        }
        [TestMethod] public void EveryStandardViewFitsWithoutOverlapOrLostOrder()
        {
            var input=Drawing();input["views"]=new JArray("front","back","left","right","top","bottom","iso");
            var result=StandardDrawingLayout.Create(input);Assert.IsTrue(result.Value<bool>("fits"));
            var slots=(JArray)result["views"];Assert.AreEqual(7,slots.Count);
            for(int i=0;i<slots.Count;i++)for(int j=i+1;j<slots.Count;j++)
            {
                var a=slots[i]["outlineMm"].Values<double>().ToArray();var b=slots[j]["outlineMm"].Values<double>().ToArray();
                Assert.IsTrue(a[2]+15<=b[0]+1e-8||b[2]+15<=a[0]+1e-8||a[3]+15<=b[1]+1e-8||b[3]+15<=a[1]+1e-8);
            }
        }
        [TestMethod] public void FixedScaleReportsNoFitAndAutomaticScaleReduces()
        {
            var input=Drawing();input["modelSizeMm"]=new JArray(1000,600,200);input["fixedScale"]=1;
            var result=StandardDrawingLayout.Create(input);Assert.IsFalse(result.Value<bool>("fits"));Assert.AreEqual(1,result.Value<double>("scale"));
            input.Remove("fixedScale");result=StandardDrawingLayout.Create(input);Assert.IsTrue(result.Value<bool>("fits"));Assert.IsTrue(result.Value<double>("scale")<1);
            input["sheetWidthMm"]=50;input["sheetHeightMm"]=50;result=StandardDrawingLayout.Create(input);
            Assert.IsFalse(result.Value<bool>("fits")); // Reserved title band leaves no usable height.
        }
        [TestMethod] public void ExplicitUsableAreaOverridesDefaultFormatReservation()
        {
            var input=Drawing();input["usableMm"]=new JArray(10,10,410,287);input["views"]=new JArray("front");
            var result=StandardDrawingLayout.Create(input);
            Assert.AreEqual(210,Slot(result,"front").Value<double>("centerXmm"),1e-10);
            Assert.AreEqual(148.5,Slot(result,"front").Value<double>("centerYmm"),1e-10);
        }
        [DataTestMethod][DataRow(false)][DataRow(true)]
        public void ZeroHeightUsableAreaNeverFitsTinyViews(bool fixedScale)
        {
            var input=Drawing();input["sheetWidthMm"]=50;input["sheetHeightMm"]=50;
            input["modelSizeMm"]=new JArray(1,.0001,1);input["views"]=new JArray("front");
            if(fixedScale)input["fixedScale"]=.0001;
            var result=StandardDrawingLayout.Create(input);
            Assert.AreEqual(result["usableMm"][1].Value<double>(),result["usableMm"][3].Value<double>());
            Assert.IsFalse(result.Value<bool>("fits"),"Tolerance cannot make a zero-height usable area fit a positive view.");
        }
        [DataTestMethod]
        [DataRow("views", "[]")][DataRow("views", "['front','front']")][DataRow("views", "['banana']")]
        [DataRow("modelSizeMm", "[0,1,2]")][DataRow("modelSizeMm", "[1,1,1e308]")]
        [DataRow("sheetWidthMm", "49")][DataRow("sheetHeightMm", "5001")]
        [DataRow("projection", "'perspective'")][DataRow("fixedScale", "0")][DataRow("sheetScale", "'1:2'")]
        [DataRow("usableMm", "[-1,0,100,100]")][DataRow("usableMm", "[100,100,10,10]")][DataRow("unknown", "false")]
        public void InvalidDrawingInputsAreRejected(string key,string json)
        {var input=Drawing();input[key]=JToken.Parse(json);Assert.ThrowsException<ArgumentException>(()=>StandardDrawingLayout.Create(input));}
        private static JObject Slot(JObject result,string view)=>(JObject)((JArray)result["views"]).Single(v=>v.Value<string>("view")==view);
    }
}
