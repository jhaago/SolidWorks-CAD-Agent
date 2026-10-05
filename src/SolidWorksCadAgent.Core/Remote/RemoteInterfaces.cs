using System;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.Core.Remote
{
    public interface IRemoteClock { DateTimeOffset UtcNow { get; } }
    public sealed class RemoteClock : IRemoteClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    public interface IRemoteInputSink
    {
        bool Apply(RemoteInputEvent input);
        bool ReleaseAll();
    }
    public interface IRemoteCapture { RemoteCapturedFrame Capture(); }
}
