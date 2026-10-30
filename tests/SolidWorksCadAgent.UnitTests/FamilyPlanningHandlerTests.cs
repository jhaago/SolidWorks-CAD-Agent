using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.SolidWorksBridge.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FamilyPlanningHandlerTests
    {
        [TestMethod] public async Task BothPlanningHandlersReturnPredictionsWithoutSessionOrDocument()
        {
            var pattern=new ComponentPatternPlanCommandHandler();
            var input=JObject.Parse("{'pattern':'linear','seeds':[{'id':'bolt','originMm':[0,0,0]}],'direction':'x','spacingMm':20,'count':3}");
            Assert.IsNull(pattern.Validate(input));
            var result=await pattern.ExecuteAsync(input,CancellationToken.None);
            Assert.IsTrue(result.Success);Assert.AreEqual(2,((JArray)result.Data["placements"]).Count);
            Assert.IsFalse(result.Data.Value<bool>("nativeCreated"));
            var drawing=new StandardDrawingLayoutCommandHandler();
            var layout=await drawing.ExecuteAsync(JObject.Parse("{'sheetWidthMm':420,'sheetHeightMm':297,'modelSizeMm':[100,60,20],'views':['front','top']}"),CancellationToken.None);
            Assert.IsTrue(layout.Success);Assert.IsTrue(layout.Data.Value<bool>("planningOnly"));
        }
        [TestMethod] public async Task InvalidInputsHaveStructuredErrorsEvenWhenCalledDirectly()
        {
            var pattern=new ComponentPatternPlanCommandHandler();
            Assert.AreEqual("INVALID_PARAMETERS",pattern.Validate(new JObject()).Code);
            Assert.AreEqual("INVALID_PARAMETERS",(await pattern.ExecuteAsync(new JObject(),CancellationToken.None)).Error.Code);
            var drawing=new StandardDrawingLayoutCommandHandler();
            Assert.AreEqual("INVALID_PARAMETERS",drawing.Validate(null).Code);
            Assert.AreEqual("INVALID_PARAMETERS",(await drawing.ExecuteAsync(null,CancellationToken.None)).Error.Code);
        }
        [TestMethod] public async Task CancellationPreventsPlanning()
        {
            var token=new CancellationToken(true);
            await Assert.ThrowsExceptionAsync<System.OperationCanceledException>(()=>new ComponentPatternPlanCommandHandler().ExecuteAsync(new JObject(),token));
            await Assert.ThrowsExceptionAsync<System.OperationCanceledException>(()=>new StandardDrawingLayoutCommandHandler().ExecuteAsync(new JObject(),token));
        }
    }
}
