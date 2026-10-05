using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    internal sealed class TestRemoteSink : IRemoteInputSink
    {
        public int Applied, Released;
        public bool ApplyResult = true, ReleaseResult = true;
        public Action BeforeApply;
        public bool Apply(RemoteInputEvent input) { BeforeApply?.Invoke(); Applied++; return ApplyResult; }
        public bool ReleaseAll() { Released++; return ReleaseResult; }
    }
    internal sealed class SessionFixture
    {
        public readonly TestRemoteClock Clock=new TestRemoteClock();
        public readonly TestRemoteSink Sink=new TestRemoteSink();
        public readonly RemotePairingCoordinator Pairing;
        public readonly RemoteSessionCoordinator Sessions;
        public readonly PairingResult Device;
        public readonly SessionGrant Grant;
        public SessionFixture() {
            Pairing=new RemotePairingCoordinator(Clock,new MemoryRemoteStore());
            var receipt=Pairing.RequestPairing(Pairing.OpenPairing().Secret,"phone"); Pairing.Approve(receipt.RequestId);
            Device=Pairing.Poll(receipt.RequestId,receipt.ReceiptSecret);
            Sessions=new RemoteSessionCoordinator(Clock,Pairing,Sink); Sessions.UpdateDisplayGeneration(1);
            Grant=Sessions.CreateSession(Device.DeviceId,Device.Credential);
        }
        public RemoteInputEvent Input(long sequence=1) {
            var state=Sessions.Status(Grant.SessionToken);
            return new RemoteInputEvent { SessionId=state.SessionId, AuthorityEpoch=state.AuthorityEpoch, Sequence=sequence, DisplayGeneration=1, Kind="down",Button="primary",X=.5,Y=.5 };
        }
    }
    [TestClass]
    public class RemoteSessionTests
    {
        [TestMethod] public void ExpiredOrRevokedTokenCannotReadOrControl() {
            var f=new SessionFixture(); f.Clock.UtcNow+=TimeSpan.FromMinutes(5);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.Status(f.Grant.SessionToken));
            f=new SessionFixture(); f.Pairing.Revoke(f.Device.DeviceId);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.Status(f.Grant.SessionToken));
            Assert.AreEqual(0,f.Sink.Applied);
        }
        [TestMethod] public void SecondDeviceCannotDisplaceController() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.CreateSession(f.Device.DeviceId,f.Device.Credential));
            Assert.IsTrue(f.Sessions.Status(f.Grant.SessionToken).Controlling);
        }
        [TestMethod] public void DuplicateAndWrongEpochInputNeverCallsSink() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); var input=f.Input();
            Assert.IsTrue(f.Sessions.SubmitInput(f.Grant.SessionToken,input));
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
            input=f.Input(2); input.AuthorityEpoch--;
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
            input=f.Input(3); Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
            Assert.AreEqual(1,f.Sink.Applied);
        }
        [TestMethod] public void HeartbeatLossAtThreeSecondsReleasesAndRevokes() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); f.Sessions.SubmitInput(f.Grant.SessionToken,f.Input());
            f.Clock.UtcNow+=TimeSpan.FromSeconds(2.9); f.Sessions.Tick(); Assert.IsTrue(f.Sessions.Status(f.Grant.SessionToken).Controlling);
            f.Clock.UtcNow+=TimeSpan.FromSeconds(.1); f.Sessions.Tick();
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.Status(f.Grant.SessionToken)); Assert.IsTrue(f.Sink.Released>0);
        }
        [TestMethod] public void DisplayGenerationChangeReleasesAndRejectsOldInput() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); var input=f.Input(); f.Sessions.UpdateDisplayGeneration(2);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
            Assert.IsFalse(f.Sessions.Status(f.Grant.SessionToken).Controlling); Assert.AreEqual(0,f.Sink.Applied);
        }
        [TestMethod] public void FailedReleaseDisablesControl() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); f.Sink.ReleaseResult=false; f.Sessions.StopRemote();
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.CreateSession(f.Device.DeviceId,f.Device.Credential));
        }
        [TestMethod] public void ReconnectRequiresResumeAndClearsHeldInput() {
            var f=new SessionFixture(); Assert.IsFalse(f.Grant.Session.Controlling); f.Sessions.ResumeControl(f.Grant.SessionToken);
            var old=f.Input(); f.Sessions.Close(f.Grant.SessionToken); var fresh=f.Sessions.CreateSession(f.Device.DeviceId,f.Device.Credential);
            Assert.IsFalse(fresh.Session.Controlling); Assert.AreNotEqual(f.Grant.Session.SessionId,fresh.Session.SessionId);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(fresh.SessionToken,old)); Assert.AreEqual(0,f.Sink.Applied);
        }
        [TestMethod] public void RenewalRequiresCredentialAndInvalidatesOldToken() {
            var f=new SessionFixture();
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.RenewSession(f.Grant.SessionToken,f.Device.DeviceId,"wrong"));
            var renewed=f.Sessions.RenewSession(f.Grant.SessionToken,f.Device.DeviceId,f.Device.Credential);
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.Status(f.Grant.SessionToken)); Assert.IsNotNull(f.Sessions.Status(renewed.SessionToken));
        }
        [TestMethod] public void FailedInputDeliveryRevokesSession() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); f.Sink.ApplyResult=false;
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,f.Input()));
            Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.Status(f.Grant.SessionToken));
        }
        [TestMethod] public void StopRacingInputCannotInjectAfterRevocation() {
            var f=new SessionFixture(); f.Sessions.ResumeControl(f.Grant.SessionToken); var input=f.Input();
            using(var entered=new ManualResetEventSlim()) using(var finish=new ManualResetEventSlim()) {
                f.Sink.BeforeApply=()=>{entered.Set(); if(!finish.Wait(3000)) throw new TimeoutException();};
                var apply=Task.Run(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
                Assert.IsTrue(entered.Wait(3000)); var stop=Task.Run(()=>f.Sessions.StopRemote()); finish.Set();
                Assert.IsTrue(Task.WaitAll(new Task[]{apply,stop},3000));
                Assert.ThrowsException<RemoteProtocolException>(()=>f.Sessions.SubmitInput(f.Grant.SessionToken,input));
                Assert.AreEqual(1,f.Sink.Applied); Assert.IsTrue(f.Sink.Released>0);
            }
        }
    }
}
