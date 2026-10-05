using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.Core.Remote
{
    [JsonObject(NamingStrategyType=typeof(CamelCaseNamingStrategy))]
    public sealed class SessionGrant { public string SessionToken { get; set; } public RemoteSessionSnapshot Session { get; set; } }
    public sealed class RemoteSessionCoordinator : IDisposable
    {
        private readonly object gate=new object();
        private readonly IRemoteClock clock;
        private readonly RemotePairingCoordinator pairing;
        private readonly IRemoteInputSink input;
        private readonly IRemoteSecretGenerator secrets;
        private ActiveSession active;
        private long nextEpoch, generation;
        private bool healthy=true;
        private sealed class ActiveSession {
            public string Id, Device, TokenVerifier;
            public long Epoch, Sequence;
            public bool Controlling;
            public DateTimeOffset Expires, Heartbeat;
        }
        public RemoteSessionCoordinator(IRemoteClock clock,RemotePairingCoordinator pairing,IRemoteInputSink input,IRemoteSecretGenerator secrets=null) {
            this.clock=clock; this.pairing=pairing; this.input=input; this.secrets=secrets??new RemoteSecretGenerator(); pairing.Revoked+=OnRevoked;
        }
        public SessionGrant CreateSession(string deviceId,string credential) {
            lock(gate) {
                Expire(); EnsureHealthy();
                if(!pairing.Authenticate(deviceId,credential)) throw Error(401,"device_invalid","Pair this device again on Windows.");
                if(active!=null) throw Error(409,"session_busy","Another remote session is active. Stop it on Windows or wait for it to disconnect.");
                string token=RemoteSecrets.New(secrets);
                active=new ActiveSession { Id=Guid.NewGuid().ToString("N"),Device=deviceId,TokenVerifier=RemoteSecrets.Hash(token),Epoch=++nextEpoch,Expires=clock.UtcNow.AddMinutes(5),Heartbeat=clock.UtcNow };
                return new SessionGrant { SessionToken=token,Session=Snapshot() };
            }
        }
        public SessionGrant RenewSession(string token,string deviceId,string credential) {
            lock(gate) {
                var session=Authorize(token);
                if(session.Device!=deviceId || !pairing.Authenticate(deviceId,credential)) throw Error(401,"device_invalid","Device authorization failed.");
                string fresh=RemoteSecrets.New(secrets); session.TokenVerifier=RemoteSecrets.Hash(fresh); session.Expires=clock.UtcNow.AddMinutes(5);
                return new SessionGrant { SessionToken=fresh,Session=Snapshot() };
            }
        }
        public RemoteSessionSnapshot Status(string token) { lock(gate) { Authorize(token); return Snapshot(); } }
        public RemoteSessionSnapshot Heartbeat(string token) { lock(gate) { Authorize(token).Heartbeat=clock.UtcNow; return Snapshot(); } }
        public RemoteSessionSnapshot ResumeControl(string token) {
            lock(gate) { Authorize(token); EnsureHealthy(); if(generation<1) throw Error(409,"display_unavailable","Wait for a current desktop image before taking control."); ResetAuthority(); active.Controlling=true; return Snapshot(); }
        }
        public RemoteSessionSnapshot TakeControl(string token) { return ResumeControl(token); }
        public bool SubmitInput(string token,RemoteInputEvent value) {
            lock(gate) {
                var session=Authorize(token); EnsureHealthy();
                if(value==null || !value.IsValid()) throw Error(400,"input_invalid","This input event is unsupported or invalid.");
                if(!session.Controlling || value.SessionId!=session.Id || value.AuthorityEpoch!=session.Epoch || value.DisplayGeneration!=generation || value.Sequence!=session.Sequence+1)
                    throw Error(409,"input_stale","Input authority or display changed. Take control again.");
                session.Sequence=value.Sequence; // Consume before OS delivery: a lost reply must never replay input.
                bool applied; try { applied=input.Apply(value); } catch { applied=false; }
                if(!applied) { StopLocked(); throw Error(502,"input_failed","Windows could not deliver input. Remote control stopped."); }
                return true;
            }
        }
        public void Close(string token) { lock(gate) { Authorize(token); StopLocked(); } }
        public void StopRemote() { lock(gate) StopLocked(); }
        public void Tick() { lock(gate) Expire(); }
        public string LocalStatusText { get { lock(gate) { Expire(); return active==null ? "No remote session" : active.Controlling ? "Remote manual control active" : "Remote viewing only"; } } }
        public void UpdateDisplayGeneration(long value) {
            lock(gate) {
                if(value<1) throw new ArgumentOutOfRangeException(nameof(value));
                if(value==generation) return; generation=value;
                if(active!=null) ResetAuthority();
            }
        }
        public void ReleaseInput(string token) { lock(gate) { Authorize(token); ResetAuthority(); } }
        private void ResetAuthority() {
            active.Controlling=false; active.Sequence=0; active.Epoch=++nextEpoch;
            if(!Release()) { active=null; throw Error(503,"release_failed","Windows could not release remote input. Restart the remote agent before taking control."); }
        }
        private bool Release() { try { if(!input.ReleaseAll()) healthy=false; } catch { healthy=false; } return healthy; }
        private void StopLocked() { Release(); active=null; nextEpoch++; }
        private void Expire() {
            if(active!=null && (clock.UtcNow>=active.Expires || clock.UtcNow-active.Heartbeat>=TimeSpan.FromSeconds(3) || !pairing.IsDeviceActive(active.Device))) StopLocked();
        }
        private ActiveSession Authorize(string token) {
            Expire();
            if(active==null || !RemoteSecrets.Matches(active.TokenVerifier,token)) throw Error(401,"session_invalid","The remote session ended. Reconnect and resume control.");
            return active;
        }
        private void EnsureHealthy() { if(!healthy) throw Error(503,"release_failed","Remote input release failed. Restart the Windows remote agent."); }
        private RemoteSessionSnapshot Snapshot()=>new RemoteSessionSnapshot { SessionId=active.Id,AuthorityEpoch=active.Epoch,Controlling=active.Controlling,ExpiresAt=active.Expires };
        private void OnRevoked(string device) { lock(gate) { if(active!=null && (device=="*" || device==active.Device)) StopLocked(); } }
        public void Dispose() { pairing.Revoked-=OnRevoked; StopRemote(); }
        private static RemoteProtocolException Error(int status,string code,string message)=>new RemoteProtocolException(status,code,message);
    }
}
