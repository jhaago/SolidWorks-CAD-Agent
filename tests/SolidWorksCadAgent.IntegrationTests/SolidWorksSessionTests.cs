using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass]
    [TestCategory("SolidWorksIntegration")]
    public class SolidWorksSessionTests
    {
        [TestMethod]
        public async Task Lifecycle_ClosedToLaunchToAttach_ReportsVersionAndVisibleSession()
        {
            var expectedYearText = Environment.GetEnvironmentVariable("SOLIDWORKS_EXPECTED_YEAR") ?? "2020";
            Assert.IsTrue(int.TryParse(expectedYearText, out var expectedYear),
                "SOLIDWORKS_EXPECTED_YEAR must be a four-digit year such as 2020.");

            using (var launcherSession = new SolidWorksSession())
            {
                var beforeLaunch = await launcherSession.AttachAsync(CancellationToken.None);
                Assert.IsFalse(beforeLaunch.IsConnected,
                    "Close all SOLIDWORKS instances before running this lifecycle integration test.");
                Assert.IsFalse(beforeLaunch.IsRunning);

                var launched = await launcherSession.LaunchAsync(CancellationToken.None);
                Assert.IsTrue(launched.IsConnected, launched.ErrorMessage);
                Assert.IsTrue(launched.IsRunning);
                Assert.IsTrue(launched.IsVisible, "SOLIDWORKS must remain visible in V1.");
                Assert.IsNotNull(launched.RuntimeInfo);
                Assert.AreEqual(expectedYear, launched.RuntimeInfo.ReleaseYear);
                Assert.IsNotNull(launched.Compatibility);
                Assert.IsTrue(launched.Compatibility.CanAttemptV1Commands,
                    launched.Compatibility.Message);

                var status = await launcherSession.GetStatusAsync(CancellationToken.None);
                Assert.IsTrue(status.IsConnected);
                Assert.AreEqual(launched.DispatcherThreadId, status.DispatcherThreadId,
                    "All session COM work must stay on the same dedicated STA thread.");

                using (var attachSession = new SolidWorksSession())
                {
                    var attached = await attachSession.AttachAsync(CancellationToken.None);
                    Assert.IsTrue(attached.IsConnected, attached.ErrorMessage);
                    Assert.IsTrue(attached.IsRunning);
                    Assert.IsTrue(attached.IsVisible);
                    Assert.IsNotNull(attached.RuntimeInfo);
                    Assert.AreEqual(expectedYear, attached.RuntimeInfo.ReleaseYear);
                    Assert.IsTrue(attached.Compatibility.CanAttemptV1Commands);
                }
            }
        }
    }
}
