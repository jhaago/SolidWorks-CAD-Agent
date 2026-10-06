using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FaceSketchPlacementTests
    {
        [TestMethod]
        public void CutExtrudeDoesNotAcceptUnimplementedDirectionSemantics()
        {
            Assert.IsNull(PrismaticProfileGeometry.ValidateCut(JObject.FromObject(new { endCondition = "Blind", depthMm = 3 })));
            Assert.IsNull(PrismaticProfileGeometry.ValidateCut(JObject.FromObject(new { endCondition = "ThroughAll" })));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(new CadCommandEnvelope
            {
                Command = "CutExtrude",
                Parameters = JObject.FromObject(new { endCondition = "Blind", depthMm = 3, direction = "IntoBody" })
            }));
            Assert.IsNotNull(CadPlanningCommandContract.Validate(new CadCommandEnvelope
            {
                Command = "CutExtrude",
                Parameters = JObject.FromObject(new { endCondition = "ThroughAll", direction = "Negative" })
            }));
        }
    }
}
