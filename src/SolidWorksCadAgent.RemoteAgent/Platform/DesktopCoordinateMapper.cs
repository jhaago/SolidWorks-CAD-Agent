using System;
using System.Drawing;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    public static class DesktopCoordinateMapper
    {
        public static Point Map(double x,double y,Rectangle monitor,Rectangle desktop) {
            if(!RemoteInputEvent.Coordinate(x)||!RemoteInputEvent.Coordinate(y)||monitor.Width<1||monitor.Height<1||desktop.Width<2||desktop.Height<2 || !desktop.Contains(monitor))
                throw new ArgumentOutOfRangeException("coordinates");
            int px=monitor.Left+(int)Math.Round(x*(monitor.Width-1),MidpointRounding.AwayFromZero);
            int py=monitor.Top+(int)Math.Round(y*(monitor.Height-1),MidpointRounding.AwayFromZero);
            return new Point((int)Math.Round((px-desktop.Left)*65535.0/(desktop.Width-1)),(int)Math.Round((py-desktop.Top)*65535.0/(desktop.Height-1)));
        }
        public static Size Fit(Size size) {
            if(size.Width<1 || size.Height<1) throw new ArgumentOutOfRangeException(nameof(size));
            double scale=Math.Min(1,1600.0/Math.Max(size.Width,size.Height));
            return new Size(Math.Max(1,(int)Math.Round(size.Width*scale)),Math.Max(1,(int)Math.Round(size.Height*scale)));
        }
    }
    public sealed class DisplayGenerationTracker
    {
        private Rectangle monitor,desktop; private int dpi; private long generation;
        public long Update(Rectangle nextMonitor,Rectangle nextDesktop,int nextDpi) {
            if(generation==0||monitor!=nextMonitor||desktop!=nextDesktop||dpi!=nextDpi) {monitor=nextMonitor;desktop=nextDesktop;dpi=nextDpi;generation++;}
            return generation;
        }
    }
}
