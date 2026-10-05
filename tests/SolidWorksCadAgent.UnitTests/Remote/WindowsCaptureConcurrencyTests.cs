#if NETFRAMEWORK
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.RemoteAgent.Platform;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    [TestClass]
    public class WindowsCaptureConcurrencyTests
    {
        [TestMethod]
        public void SlowCaptureCannotBlockInputGeometryOrWatchdog()
        {
            using(var entered=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) {
                var capture=new WindowsDesktopCapture(new TestRemoteClock(),()=>new DesktopGeometry {Generation=1},geometry=>{
                    entered.Set();if(!release.Wait(3000))throw new TimeoutException();return null;
                });
                var image=Task.Run(()=>capture.Capture());
                try {
                    Assert.IsTrue(entered.Wait(3000));
                    var metadata=Task.Run(()=>capture.Geometry());
                    Assert.IsTrue(metadata.Wait(500),"Input geometry must remain available while an image encoder is blocked.");
                    Assert.AreEqual(1L,metadata.Result.Generation);
                } finally {release.Set();image.Wait(3000);}
            }
        }
    }
}
#endif
