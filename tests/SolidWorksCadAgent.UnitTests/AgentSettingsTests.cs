using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class AgentSettingsTests
    {
        [TestMethod]
        public void Defaults_AreLocalApprovalFirstAndWorkspaceRestricted()
        {
            var type = Type.GetType(
                "SolidWorksCadAgent.Core.AgentSettings, SolidWorksCadAgent.Core",
                throwOnError: false);
            Assert.IsNotNull(type, "AgentSettings must exist.");

            var settings = Activator.CreateInstance(type);

            Assert.AreEqual(
                @"C:\SolidWorks-CAD-Agent\Workspace",
                type.GetProperty("WorkspaceRoot")?.GetValue(settings));
            Assert.AreEqual(
                false,
                type.GetProperty("AutoMode")?.GetValue(settings));
            Assert.AreEqual(
                "http://127.0.0.1:53741/",
                type.GetProperty("HostPrefix")?.GetValue(settings));
            Assert.AreEqual(
                "gpt-5.6-sol",
                type.GetProperty("OpenAiModel")?.GetValue(settings));
        }
    }
}
