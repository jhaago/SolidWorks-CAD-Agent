using System;
using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.RemoteAgent.Platform;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    [TestClass]
    public class DesktopCoordinateMapperTests
    {
        [TestMethod] public void MappingHandlesNegativeMonitorOriginsAndEdges() {
            var monitor=new Rectangle(-1920,0,1920,1080); var desktop=new Rectangle(-1920,0,3840,1080);
            Assert.AreEqual(new Point(0,0),DesktopCoordinateMapper.Map(0,0,monitor,desktop));
            Assert.AreEqual(new Point(32759,65535),DesktopCoordinateMapper.Map(1,1,monitor,desktop));
            Assert.ThrowsException<ArgumentOutOfRangeException>(()=>DesktopCoordinateMapper.Map(double.NaN,0,monitor,desktop));
        }
        [TestMethod] public void ResizePreservesAspectRatioAnd1600Limit() {
            Assert.AreEqual(new Size(1600,900),DesktopCoordinateMapper.Fit(new Size(3840,2160)));
            Assert.AreEqual(new Size(900,1600),DesktopCoordinateMapper.Fit(new Size(2160,3840)));
            Assert.AreEqual(new Size(100,60),DesktopCoordinateMapper.Fit(new Size(100,60)));
        }
        [TestMethod] public void GenerationChangesOnMonitorLayoutOrScale() {
            var geometry=new DisplayGenerationTracker(); var monitor=new Rectangle(0,0,1920,1080);
            Assert.AreEqual(1L,geometry.Update(monitor,monitor,96)); Assert.AreEqual(1L,geometry.Update(monitor,monitor,96));
            Assert.AreEqual(2L,geometry.Update(monitor,monitor,144));
            Assert.AreEqual(3L,geometry.Update(new Rectangle(1,0,1920,1080),monitor,144));
        }
        [TestMethod] public void FrameCacheDropsObsoleteFrames() {
            var clock=new TestRemoteClock(); int calls=0;
            var cache=new RemoteFrameCache(clock,()=>new RemoteCapturedFrame {FrameId=++calls,DisplayGeneration=1,Width=100,Height=60,JpegBytes=new byte[]{1},CapturedAt=clock.UtcNow});
            Assert.AreEqual(1L,cache.Capture().FrameId); Assert.AreEqual(1L,cache.Capture().FrameId); Assert.AreEqual(1,calls);
            clock.UtcNow+=TimeSpan.FromMilliseconds(200); Assert.AreEqual(2L,cache.Capture().FrameId); Assert.AreEqual(2,calls);
        }
        [TestMethod] public void OversizedJpegIsRejected() {
            var cache=new RemoteFrameCache(new TestRemoteClock(),()=>new RemoteCapturedFrame {Width=100,Height=60,JpegBytes=new byte[2097153]});
            Assert.IsNull(cache.Capture());
        }
        [TestMethod] public void FailedCaptureDoesNotReturnOldImage() {
            var clock=new TestRemoteClock(); bool fail=false;
            var cache=new RemoteFrameCache(clock,()=>{if(fail)throw new InvalidOperationException();return new RemoteCapturedFrame {Width=100,Height=60,JpegBytes=new byte[]{1}};});
            Assert.IsNotNull(cache.Capture()); fail=true; clock.UtcNow+=TimeSpan.FromMilliseconds(200); Assert.IsNull(cache.Capture());
        }
    }
}
