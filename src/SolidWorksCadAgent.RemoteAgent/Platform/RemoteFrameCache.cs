using System;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    public sealed class RemoteFrameCache : IRemoteCapture
    {
        private readonly object gate=new object(); private readonly IRemoteClock clock;
        private readonly Func<RemoteCapturedFrame> capture; private RemoteCapturedFrame latest;
        private DateTimeOffset lastAttempt=DateTimeOffset.MinValue;
        public RemoteFrameCache(IRemoteClock clock,Func<RemoteCapturedFrame> capture) {this.clock=clock;this.capture=capture;}
        public RemoteCapturedFrame Capture() {
            lock(gate) {
                if(clock.UtcNow-lastAttempt<TimeSpan.FromMilliseconds(200)) return latest;
                lastAttempt=clock.UtcNow; latest=null;
                try {
                    var value=capture();
                    if(value!=null&&value.Width>0&&value.Height>0&&value.Width<=1600&&value.Height<=1600&&value.JpegBytes!=null&&value.JpegBytes.Length>0&&value.JpegBytes.Length<=2097152) latest=value;
                } catch { }
                return latest;
            }
        }
    }
}
