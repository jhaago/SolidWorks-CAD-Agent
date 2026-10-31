using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SubmissionInputHashTests
    {
        [TestMethod]
        public void Hash_TrimsPromptButDistinguishesImagePresenceTypeAndBytes()
        {
            var plain = SubmissionInputHash.Compute("plate", null);
            Assert.AreEqual(plain, SubmissionInputHash.Compute(" plate ", null));
            Assert.AreNotEqual(plain, SubmissionInputHash.Compute("bracket", null));
            var image = new CadPlanningImage { MediaType = "image/jpeg", Bytes = new byte[] { 1, 2, 3 } };
            var original = SubmissionInputHash.Compute("plate", image);
            Assert.AreNotEqual(plain, original);
            Assert.AreNotEqual(original, SubmissionInputHash.Compute("plate", new CadPlanningImage { MediaType = "image/png", Bytes = image.Bytes }));
            Assert.AreNotEqual(original, SubmissionInputHash.Compute("plate", new CadPlanningImage { MediaType = image.MediaType, Bytes = new byte[] { 1, 2, 4 } }));
            Assert.AreNotEqual(original, SubmissionInputHash.Compute("plate!", image));
        }
    }
}
