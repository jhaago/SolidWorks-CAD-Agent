using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SolidWorksCompatibilityTests
    {
        [TestMethod]
        public void Evaluate_2020_IsCertifiedBaseline()
        {
            var info = SolidWorksVersionParser.ParseRevision("28.0.0");
            var result = SolidWorksCompatibility.Evaluate(info);

            Assert.AreEqual(SolidWorksCompatibilityLevel.Certified, result.Level);
            Assert.IsTrue(result.CanAttemptV1Commands);
            Assert.IsTrue(result.RequiresRegressionCertification == false);
        }

        [TestMethod]
        public void Evaluate_NewerRelease_IsForwardCompatibleButUncertified()
        {
            var info = SolidWorksVersionParser.ParseRevision("31.0.0");
            var result = SolidWorksCompatibility.Evaluate(info);

            Assert.AreEqual(SolidWorksCompatibilityLevel.ForwardCompatibleUncertified, result.Level);
            Assert.IsTrue(result.CanAttemptV1Commands);
            Assert.IsTrue(result.RequiresRegressionCertification);
        }

        [TestMethod]
        public void Evaluate_OlderRelease_IsNotPartOfCertifiedSupportRange()
        {
            var info = SolidWorksVersionParser.ParseRevision("27.5.0");
            var result = SolidWorksCompatibility.Evaluate(info);

            Assert.AreEqual(SolidWorksCompatibilityLevel.OlderUncertified, result.Level);
            Assert.IsFalse(result.CanAttemptV1Commands);
            Assert.IsTrue(result.RequiresRegressionCertification);
        }
    }
}
