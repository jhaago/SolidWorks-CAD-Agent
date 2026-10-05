#if NETFRAMEWORK
using System;
using System.Drawing;
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
        [TestMethod]
        public void CaptureTimestampPrecedesSlowImageEncoding()
        {
            var clock=new TestRemoteClock(); var started=clock.UtcNow;
            var capture=new WindowsDesktopCapture(clock,()=>new DesktopGeometry {Generation=1},geometry=>{
                clock.UtcNow+=TimeSpan.FromSeconds(4);
                return new SolidWorksCadAgent.Contracts.Remote.RemoteCapturedFrame {Width=1,Height=1,DisplayGeneration=geometry.Generation,JpegBytes=new byte[]{1}};
            });
            Assert.AreEqual(started,capture.Capture().CapturedAt,"Slow encoding must not make old pixels appear new.");
        }
        [TestMethod]
        public void PrimaryMonitorScaleChangeInvalidatesGenerationWithoutPixelBoundsChange()
        {
            int effectiveDpi=96; var monitor=new Rectangle(0,0,1920,1080);
            var capture=new WindowsDesktopCapture(new TestRemoteClock(),()=>monitor,()=>monitor,_=>effectiveDpi);
            var first=capture.Geometry().Generation;
            Assert.AreEqual(first,capture.Geometry().Generation);
            effectiveDpi=144;
            Assert.IsTrue(capture.Geometry().Generation>first,"The capture adapter must observe monitor scale, not just pixel bounds.");
        }
    }
}
#endif
