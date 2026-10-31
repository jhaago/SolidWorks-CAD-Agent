using System;
using System.IO;
using System.Reflection;
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
            Assert.AreEqual(
                "Real",
                type.GetProperty("ExecutionMode")?.GetValue(settings)?.ToString());
            Assert.IsNull(type.GetProperty("SolidWorksExecutablePath")?.GetValue(settings));
            Assert.AreEqual(
                "Information",
                type.GetProperty("LoggingLevel")?.GetValue(settings));
        }

        [TestMethod]
        public void JsonStore_RoundTripsSettingsAndIgnoresInterruptedTemporaryWrite()
        {
            var storeType = Type.GetType(
                "SolidWorksCadAgent.AgentHost.Configuration.JsonAgentSettingsStore, SolidWorksCadAgent.AgentHost",
                throwOnError: false);
            Assert.IsNotNull(storeType, "JsonAgentSettingsStore must exist.");

            var path = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = Activator.CreateInstance(storeType, path);
                var settings = storeType.GetMethod("Load").Invoke(store, null);
                var settingsType = settings.GetType();
                settingsType.GetProperty("WorkspaceRoot").SetValue(settings, @"D:\CAD Workspace");
                settingsType.GetProperty("AutoMode").SetValue(settings, true);
                settingsType.GetProperty("OpenAiModel").SetValue(settings, "gpt-test");
                settingsType.GetProperty("SolidWorksExecutablePath").SetValue(settings, @"C:\Program Files\SOLIDWORKS\SLDWORKS.exe");
                settingsType.GetProperty("LoggingLevel").SetValue(settings, "Debug");
                settingsType.GetProperty("ExecutionMode").SetValue(
                    settings,
                    Enum.Parse(settingsType.GetProperty("ExecutionMode").PropertyType, "Simulation"));

                storeType.GetMethod("Save").Invoke(store, new[] { settings });
                File.WriteAllText(path + ".tmp", "{ interrupted");

                var loaded = storeType.GetMethod("Load").Invoke(store, null);
                Assert.AreEqual(@"D:\CAD Workspace", settingsType.GetProperty("WorkspaceRoot").GetValue(loaded));
                Assert.AreEqual(true, settingsType.GetProperty("AutoMode").GetValue(loaded));
                Assert.AreEqual("gpt-test", settingsType.GetProperty("OpenAiModel").GetValue(loaded));
                Assert.AreEqual("Simulation", settingsType.GetProperty("ExecutionMode").GetValue(loaded).ToString());
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + ".tmp");
                TryDelete(path + ".bak");
            }
        }

        [TestMethod]
        public void JsonStore_InvalidSettingsAreRejectedWithoutReplacingLastGoodFile()
        {
            var storeType = Type.GetType(
                "SolidWorksCadAgent.AgentHost.Configuration.JsonAgentSettingsStore, SolidWorksCadAgent.AgentHost",
                throwOnError: false);
            Assert.IsNotNull(storeType, "JsonAgentSettingsStore must exist.");

            var path = Path.Combine(Path.GetTempPath(), "SolidWorksCadAgent-Settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = Activator.CreateInstance(storeType, path);
                var valid = storeType.GetMethod("Load").Invoke(store, null);
                storeType.GetMethod("Save").Invoke(store, new[] { valid });
                var before = File.ReadAllText(path);
                valid.GetType().GetProperty("WorkspaceRoot").SetValue(valid, "   ");

                var error = Assert.ThrowsException<TargetInvocationException>(() =>
                    storeType.GetMethod("Save").Invoke(store, new[] { valid }));

                Assert.IsInstanceOfType(error.InnerException, typeof(ArgumentException));
                Assert.AreEqual(before, File.ReadAllText(path));
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + ".tmp");
                TryDelete(path + ".bak");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
