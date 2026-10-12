using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.SolidWorksBridge.Threading;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class SolidWorksBridgeFoundationTests
    {
        [TestMethod]
        public async Task InvokeAsync_Twice_UsesSameStaThread()
        {
            using (var dispatcher = new SolidWorksStaDispatcher())
            {
                var first = await dispatcher.InvokeAsync(
                    () => new ThreadObservation
                    {
                        ThreadId = Thread.CurrentThread.ManagedThreadId,
                        Apartment = Thread.CurrentThread.GetApartmentState()
                    },
                    CancellationToken.None);

                var second = await dispatcher.InvokeAsync(
                    () => new ThreadObservation
                    {
                        ThreadId = Thread.CurrentThread.ManagedThreadId,
                        Apartment = Thread.CurrentThread.GetApartmentState()
                    },
                    CancellationToken.None);

                Assert.AreEqual(first.ThreadId, second.ThreadId);
                Assert.AreEqual(ApartmentState.STA, first.Apartment);
                Assert.AreEqual(ApartmentState.STA, second.Apartment);
            }
        }

        [TestMethod]
        public void ParseRevision_SolidWorks2020Sp0_Reports2020Baseline()
        {
            var info = SolidWorksVersionParser.ParseRevision("28.0.0");

            Assert.AreEqual("28.0.0", info.RevisionNumber);
            Assert.AreEqual(28, info.MajorRevision);
            Assert.AreEqual(2020, info.ReleaseYear);
            Assert.AreEqual(0, info.ServicePack);
            Assert.AreEqual(0, info.ServicePackHotfix);
            Assert.AreEqual("SOLIDWORKS 2020 SP0.0", info.DisplayVersion);
        }

        [TestMethod]
        public void ParseRevision_LaterRelease_RemainsVersionIndependent()
        {
            var info = SolidWorksVersionParser.ParseRevision("31.5.1");

            Assert.AreEqual(2023, info.ReleaseYear);
            Assert.AreEqual(5, info.ServicePack);
            Assert.AreEqual(1, info.ServicePackHotfix);
            Assert.AreEqual("SOLIDWORKS 2023 SP5.1", info.DisplayVersion);
        }

        private sealed class ThreadObservation
        {
            public int ThreadId { get; set; }
            public ApartmentState Apartment { get; set; }
        }
    }
}
