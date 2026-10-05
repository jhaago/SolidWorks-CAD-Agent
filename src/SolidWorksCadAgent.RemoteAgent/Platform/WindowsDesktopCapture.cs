using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    public sealed class DesktopGeometry { public Rectangle Monitor,VirtualDesktop; public long Generation; }
    public sealed class WindowsDesktopCapture
    {
        private readonly object gate=new object(); private readonly DisplayGenerationTracker tracker=new DisplayGenerationTracker();
        private readonly IRemoteClock clock; private long frameId;
        private readonly Func<DesktopGeometry> geometrySource;
        private readonly Func<DesktopGeometry,RemoteCapturedFrame> imageSource;
        private readonly Func<Rectangle> monitorBounds,desktopBounds;
        private readonly Func<Rectangle,int> monitorDpi;
        public WindowsDesktopCapture(IRemoteClock clock) {this.clock=clock;}
        public WindowsDesktopCapture(IRemoteClock clock,Func<DesktopGeometry> geometrySource,Func<DesktopGeometry,RemoteCapturedFrame> imageSource) {
            this.clock=clock;this.geometrySource=geometrySource;this.imageSource=imageSource;
        }
        public WindowsDesktopCapture(IRemoteClock clock,Func<Rectangle> monitorBounds,Func<Rectangle> desktopBounds,Func<Rectangle,int> monitorDpi) {
            this.clock=clock;this.monitorBounds=monitorBounds;this.desktopBounds=desktopBounds;this.monitorDpi=monitorDpi;
        }
        public DesktopGeometry Geometry() {
            lock(gate) {
                if(geometrySource!=null)return geometrySource();
                if(monitorBounds==null && !DesktopAvailability.IsInteractive()) throw new InvalidOperationException("Unlock Windows and close secure desktop prompts.");
                var monitor=monitorBounds!=null?monitorBounds():Screen.PrimaryScreen.Bounds;
                var desktop=desktopBounds!=null?desktopBounds():SystemInformation.VirtualScreen;
                int dpi=monitorDpi!=null?monitorDpi(monitor):EffectiveMonitorDpi(monitor);
                return new DesktopGeometry { Monitor=monitor,VirtualDesktop=desktop,Generation=tracker.Update(monitor,desktop,dpi) };
            }
        }
        public RemoteCapturedFrame Capture() {
                var geometry=Geometry();
                var capturedAt=clock.UtcNow;
                if(imageSource!=null) {
                    var frame=imageSource(geometry);
                    if(frame!=null) frame.CapturedAt=capturedAt;
                    return frame;
                }
                var bounds=geometry.Monitor; var size=DesktopCoordinateMapper.Fit(bounds.Size);
                using(var original=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb)) {
                    using(var graphics=Graphics.FromImage(original)) graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                    using(var scaled=new Bitmap(size.Width,size.Height,PixelFormat.Format24bppRgb)) {
                        using(var graphics=Graphics.FromImage(scaled)) {graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;graphics.DrawImage(original,new Rectangle(Point.Empty,size));}
                        using(var stream=new MemoryStream()) using(var parameters=new EncoderParameters(1)) {
                            parameters.Param[0]=new EncoderParameter(Encoder.Quality,65L);
                            scaled.Save(stream,ImageCodecInfo.GetImageEncoders().First(e=>e.FormatID==ImageFormat.Jpeg.Guid),parameters);
                            var after=Geometry(); if(after.Generation!=geometry.Generation)return null;
                            var cursor=Cursor.Position;
                            return new RemoteCapturedFrame {FrameId=Interlocked.Increment(ref frameId),DisplayGeneration=geometry.Generation,Width=size.Width,Height=size.Height,CapturedAt=capturedAt,
                                CursorX=Math.Max(0,Math.Min(1,(cursor.X-bounds.Left)/(double)Math.Max(1,bounds.Width-1))),CursorY=Math.Max(0,Math.Min(1,(cursor.Y-bounds.Top)/(double)Math.Max(1,bounds.Height-1))),JpegBytes=stream.ToArray()};
                        }
                    }
                }
        }
        private static int EffectiveMonitorDpi(Rectangle bounds) {
            var monitor=MonitorFromPoint(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2),2);
            if(monitor==IntPtr.Zero || GetDpiForMonitor(monitor,0,out var horizontal,out var vertical)!=0 || horizontal<1 || vertical<1)
                throw new InvalidOperationException("Primary monitor scale is unavailable.");
            return checked((int)horizontal);
        }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point,uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int dpiType,out uint horizontal,out uint vertical);
    }
}
