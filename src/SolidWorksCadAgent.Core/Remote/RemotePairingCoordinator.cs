using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.Core.Remote
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class PairingWindow { public string Secret { get; set; } public DateTimeOffset ExpiresAt { get; set; } }
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class PairingReceipt { public string RequestId { get; set; } public string ReceiptSecret { get; set; } }
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class PairingResult {
        public string State { get; set; } public string DeviceId { get; set; } public string Credential { get; set; }
    }
    public sealed class RemotePairingCoordinator
    {
        private readonly object gate = new object();
        private readonly IRemoteClock clock;
        private readonly IRemoteCredentialStore store;
        private readonly IRemoteSecretGenerator secrets;
        private List<RemoteCredentialRecord> records;
        private bool healthy = true;
        private string windowVerifier;
        private DateTimeOffset expires;
        private int attempts;
        private string requestId, receiptVerifier, requestedName;
        private PairingResult approved;
        public event Action<string> Revoked;

        public RemotePairingCoordinator(IRemoteClock clock, IRemoteCredentialStore store, IRemoteSecretGenerator secrets = null) {
            this.clock = clock; this.store = store; this.secrets = secrets ?? new RemoteSecretGenerator();
            try {
                records = store.Load().Select(Clone).ToList();
                if (records.Count > 10 || records.Any(r => string.IsNullOrEmpty(r.DeviceId) || string.IsNullOrEmpty(r.Verifier)) || records.Select(r=>r.DeviceId).Distinct().Count()!=records.Count)
                    throw new InvalidOperationException();
            } catch { records = new List<RemoteCredentialRecord>(); healthy = false; }
        }
        public IReadOnlyList<RemoteCredentialRecord> Devices { get { lock(gate) return records.Select(r=>new RemoteCredentialRecord { DeviceId=r.DeviceId, DeviceName=r.DeviceName }).ToList(); } }
        public string PendingRequestId { get { lock(gate) return clock.UtcNow < expires && approved == null ? requestId : null; } }
        public string PendingDeviceName { get { lock(gate) return requestedName; } }

        public PairingWindow OpenPairing() {
            lock(gate) {
                EnsureHealthy(); if (records.Count >= 10) throw Error(409,"device_limit","Remove a paired device before pairing another.");
                var secret = RemoteSecrets.New(secrets); windowVerifier = RemoteSecrets.Hash(secret);
                expires = clock.UtcNow.AddMinutes(2); attempts = 0; requestId = null; approved = null; requestedName = null;
                return new PairingWindow { Secret=secret, ExpiresAt=expires };
            }
        }
        public PairingReceipt RequestPairing(string secret, string name) {
            lock(gate) {
                EnsureHealthy();
                if (clock.UtcNow >= expires || windowVerifier == null || requestId != null) throw Error(401,"pairing_closed","Open a new pairing window on Windows.");
                if (!RemoteSecrets.Matches(windowVerifier,secret)) {
                    if (++attempts >= 5) windowVerifier=null;
                    throw Error(401,"pairing_invalid","The pairing code is invalid or expired.");
                }
                if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw Error(400,"device_name","Enter a device name of 1–80 printable characters.");
                requestId=Guid.NewGuid().ToString("N"); requestedName=name.Trim(); windowVerifier=null;
                var receipt=RemoteSecrets.New(secrets); receiptVerifier=RemoteSecrets.Hash(receipt);
                return new PairingReceipt { RequestId=requestId, ReceiptSecret=receipt };
            }
        }
        public void Approve(string id) {
            lock(gate) {
                EnsureHealthy(); CheckRequest(id);
                if (approved != null) throw Error(409,"pairing_decided","Pairing was already decided.");
                string deviceId=Guid.NewGuid().ToString("N"), credential=RemoteSecrets.New(secrets);
                var next=records.Select(Clone).ToList(); next.Add(new RemoteCredentialRecord { DeviceId=deviceId, DeviceName=requestedName, Verifier=RemoteSecrets.Hash(credential) });
                try { store.Save(next); records=next; }
                catch { healthy=false; requestId=null; throw Error(503,"storage_failed","Remote pairing storage is unavailable. Restart after repairing storage."); }
                approved=new PairingResult { State="approved", DeviceId=deviceId, Credential=credential };
            }
        }
        public void Reject(string id) { lock(gate) { CheckRequest(id); requestId=null; approved=null; } }
        public PairingResult Poll(string id, string receipt) {
            lock(gate) {
                EnsureHealthy(); CheckRequest(id);
                if (!RemoteSecrets.Matches(receiptVerifier,receipt)) throw Error(401,"pairing_invalid","Pairing authorization failed.");
                if (approved==null) return new PairingResult { State="pending" };
                var result=approved; approved=null; requestId=null; receiptVerifier=null; return result;
            }
        }
        public bool Authenticate(string id, string credential) {
            lock(gate) return healthy && records.Any(r=>r.DeviceId==id && RemoteSecrets.Matches(r.Verifier,credential));
        }
        public bool IsDeviceActive(string id) { lock(gate) return healthy && records.Any(r=>r.DeviceId==id); }
        public void Revoke(string id) {
            bool failed=false;
            lock(gate) {
                EnsureHealthy(); var next=records.Where(r=>r.DeviceId!=id).Select(Clone).ToList();
                try { store.Save(next); records=next; } catch { healthy=false; failed=true; }
            }
            // Outside the pairing lock: session operations authenticate under their own lock.
            Revoked?.Invoke(failed ? "*" : id);
            if(failed) throw Error(503,"storage_failed","Device removal could not be saved. Remote access is disabled until storage is repaired.");
        }
        private void CheckRequest(string id) { if(requestId==null || requestId!=id || clock.UtcNow>=expires) throw Error(401,"pairing_closed","The pairing request has expired or was cancelled."); }
        private void EnsureHealthy() { if(!healthy) throw Error(503,"storage_failed","Remote credential storage is unavailable."); }
        private static RemoteCredentialRecord Clone(RemoteCredentialRecord r) => new RemoteCredentialRecord { DeviceId=r.DeviceId,DeviceName=r.DeviceName,Verifier=r.Verifier };
        private static RemoteProtocolException Error(int status,string code,string message)=>new RemoteProtocolException(status,code,message);
    }
}
