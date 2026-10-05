using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SolidWorksNormalLaunchTests
    {
        [TestMethod]
        public async Task BlockedProbe_DeadlineReturnsWithoutStartingAProcess()
        {
            var starts = 0;
            var blocked = new TaskCompletionSource<SolidWorksSessionStatus>();
            var run = SolidWorksNormalLaunch.RunAsync(() => blocked.Task,
                () => Task.FromResult(false), () => starts++, token => Task.CompletedTask,
                2, CancellationToken.None, TimeSpan.FromMilliseconds(50));
            Assert.AreSame(run, await Task.WhenAny(run, Task.Delay(2000)));
            var status = await run;
            Assert.IsFalse(status.IsConnected);
            StringAssert.Contains(status.ErrorMessage, "timed out");
            Assert.AreEqual(0, starts);
        }

        [TestMethod]
        public async Task BlockedProbe_CallerCancellationStopsWaiting()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var blocked = new TaskCompletionSource<SolidWorksSessionStatus>();
                var run = SolidWorksNormalLaunch.RunAsync(() => blocked.Task,
                    () => Task.FromResult(false), () => Assert.Fail("Must not start"),
                    token => Task.CompletedTask, 2, cancellation.Token);
                cancellation.Cancel();
                Assert.AreSame(run, await Task.WhenAny(run, Task.Delay(2000)));
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => run);
            }
        }

        [TestMethod]
        public async Task ExistingReadyInstance_IsAttachedWithoutStartingAnotherProcess()
        {
            var starts = 0;
            var status = await SolidWorksNormalLaunch.RunAsync(
                () => Task.FromResult(new SolidWorksSessionStatus { IsConnected = true }),
                () => Task.FromResult(true), () => starts++,
                token => Task.CompletedTask, 3, CancellationToken.None);
            Assert.IsTrue(status.IsConnected);
            Assert.AreEqual(0, starts);
        }

        [TestMethod]
        public async Task NewInstance_WaitsForRegistrationAndStartupWithoutRelaunching()
        {
            var starts = 0; var probes = 0; var readiness = 0;
            var status = await SolidWorksNormalLaunch.RunAsync(
                () => Task.FromResult(new SolidWorksSessionStatus { IsConnected = ++probes >= 3 }),
                () => Task.FromResult(++readiness >= 2), () => starts++,
                token => Task.CompletedTask, 5, CancellationToken.None);
            Assert.IsTrue(status.IsConnected);
            Assert.AreEqual(1, starts);
            Assert.IsTrue(readiness >= 2);
        }

        [TestMethod]
        public async Task MissingRegistration_TimesOutWithoutKillingOrRepeatingLaunch()
        {
            var starts = 0;
            var result = await SolidWorksNormalLaunch.RunAsync(
                () => Task.FromResult(new SolidWorksSessionStatus()), () => Task.FromResult(false),
                () => starts++, token => Task.CompletedTask, 2, CancellationToken.None);
            Assert.IsFalse(result.IsConnected);
            Assert.AreEqual(1, starts);
            StringAssert.Contains(result.ErrorMessage, "timed out");
        }

        [TestMethod]
        public async Task Cancellation_PreventsProcessStart()
        {
            var starts = 0;
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => SolidWorksNormalLaunch.RunAsync(
                    () => Task.FromResult(new SolidWorksSessionStatus()), () => Task.FromResult(false),
                    () => starts++, token => Task.CompletedTask, 2, cancel.Token));
            }
            Assert.AreEqual(0, starts);
        }

        [DataTestMethod]
        [DataRow("\"C:\\Program Files\\SOLIDWORKS\\SLDWORKS.exe\" /Automation", "C:\\Program Files\\SOLIDWORKS\\SLDWORKS.exe")]
        [DataRow("C:\\PROGRA~1\\SOLIDW~1\\SLDWORKS.exe", "C:\\PROGRA~1\\SOLIDW~1\\SLDWORKS.exe")]
        public void RegistryCommand_ExtractsOnlyExecutable(string command, string expected)
        {
            Assert.AreEqual(expected, SolidWorksNormalLaunch.ParseExecutable(command));
        }
    }
}
