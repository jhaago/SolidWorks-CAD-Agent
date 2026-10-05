using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public WindowsDesktopCapture(IRemoteClock clock) {this.clock=clock;}
        public DesktopGeometry Geometry() {
            lock(gate) {
                if(!DesktopAvailability.IsInteractive()) throw new InvalidOperationException("Unlock Windows and close secure desktop prompts.");
                var monitor=Screen.PrimaryScreen.Bounds;var desktop=SystemInformation.VirtualScreen;
                int dpi=96;try {dpi=(int)GetDpiForSystem();}catch(EntryPointNotFoundException){}
                return new DesktopGeometry { Monitor=monitor,VirtualDesktop=desktop,Generation=tracker.Update(monitor,desktop,dpi) };
            }
        }
        public RemoteCapturedFrame Capture() {
            lock(gate) {
                var geometry=Geometry(); var bounds=geometry.Monitor; var size=DesktopCoordinateMapper.Fit(bounds.Size);
                using(var original=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb)) {
                    using(var graphics=Graphics.FromImage(original)) graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                    using(var scaled=new Bitmap(size.Width,size.Height,PixelFormat.Format24bppRgb)) {
                        using(var graphics=Graphics.FromImage(scaled)) {graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;graphics.DrawImage(original,new Rectangle(Point.Empty,size));}
                        using(var stream=new MemoryStream()) using(var parameters=new EncoderParameters(1)) {
                            parameters.Param[0]=new EncoderParameter(Encoder.Quality,65L);
                            scaled.Save(stream,ImageCodecInfo.GetImageEncoders().First(e=>e.FormatID==ImageFormat.Jpeg.Guid),parameters);
                            var after=Geometry(); if(after.Generation!=geometry.Generation)return null;
                            var cursor=Cursor.Position;
                            return new RemoteCapturedFrame {FrameId=++frameId,DisplayGeneration=geometry.Generation,Width=size.Width,Height=size.Height,CapturedAt=clock.UtcNow,
                                CursorX=Math.Max(0,Math.Min(1,(cursor.X-bounds.Left)/(double)Math.Max(1,bounds.Width-1))),CursorY=Math.Max(0,Math.Min(1,(cursor.Y-bounds.Top)/(double)Math.Max(1,bounds.Height-1))),JpegBytes=stream.ToArray()};
                        }
                    }
                }
            }
        }
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    }
}
