using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    internal sealed class TestRemoteClock : IRemoteClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
    }
    internal sealed class MemoryRemoteStore : IRemoteCredentialStore
    {
        public IReadOnlyList<RemoteCredentialRecord> Records = new List<RemoteCredentialRecord>();
        public bool Fail;
        public IReadOnlyList<RemoteCredentialRecord> Load() => Records;
        public void Save(IReadOnlyList<RemoteCredentialRecord> records) {
            if (Fail) throw new InvalidOperationException("private backend exception");
            Records = records;
        }
    }
    [TestClass]
    public class RemotePairingTests
    {
        [TestMethod]
        public void PairingRequiresLocalApprovalAndReceiptSecret()
        {
            var pairing = new RemotePairingCoordinator(new TestRemoteClock(), new MemoryRemoteStore());
            var window = pairing.OpenPairing();
            var receipt = pairing.RequestPairing(window.Secret, "Jordan phone");
            Assert.IsNull(pairing.Poll(receipt.RequestId, receipt.ReceiptSecret).Credential);
            pairing.Approve(receipt.RequestId);
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.Poll(receipt.RequestId, "wrong"));
            var result = pairing.Poll(receipt.RequestId, receipt.ReceiptSecret);
            Assert.IsTrue(pairing.Authenticate(result.DeviceId, result.Credential));
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.Poll(receipt.RequestId, receipt.ReceiptSecret));
        }
        [TestMethod]
        public void PairingExpiresAfterTwoMinutesAndIsSingleUse()
        {
            var clock = new TestRemoteClock(); var pairing = new RemotePairingCoordinator(clock, new MemoryRemoteStore());
            var window = pairing.OpenPairing(); clock.UtcNow += TimeSpan.FromMinutes(2);
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.RequestPairing(window.Secret, "phone"));
            window = pairing.OpenPairing(); pairing.RequestPairing(window.Secret, "phone");
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.RequestPairing(window.Secret, "other"));
        }
        [TestMethod]
        public void PairingRateLimitClosesWindowAfterFiveFailedAttempts()
        {
            var pairing = new RemotePairingCoordinator(new TestRemoteClock(), new MemoryRemoteStore());
            var window = pairing.OpenPairing();
            for (int n = 0; n < 5; n++) Assert.ThrowsException<RemoteProtocolException>(() => pairing.RequestPairing("wrong", "phone"));
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.RequestPairing(window.Secret, "phone"));
        }
        [TestMethod]
        public void RevokePersistsBeforeReturningSuccess()
        {
            var store = new MemoryRemoteStore(); var clock = new TestRemoteClock();
            var pairing = new RemotePairingCoordinator(clock, store);
            var receipt = pairing.RequestPairing(pairing.OpenPairing().Secret, "phone");
            pairing.Approve(receipt.RequestId); var result = pairing.Poll(receipt.RequestId, receipt.ReceiptSecret);
            pairing.Revoke(result.DeviceId);
            Assert.IsFalse(new RemotePairingCoordinator(clock, store).Authenticate(result.DeviceId, result.Credential));
        }
        [TestMethod]
        public void StoreFailureCannotReportEnrollmentSuccess()
        {
            var store = new MemoryRemoteStore(); var pairing = new RemotePairingCoordinator(new TestRemoteClock(), store);
            var receipt = pairing.RequestPairing(pairing.OpenPairing().Secret, "phone"); store.Fail = true;
            var error = Assert.ThrowsException<RemoteProtocolException>(() => pairing.Approve(receipt.RequestId));
            Assert.IsFalse(error.Message.Contains("private backend"));
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.Poll(receipt.RequestId, receipt.ReceiptSecret));
        }
        [TestMethod]
        public void FailedRevocationDisablesAllAuthentication()
        {
            var store = new MemoryRemoteStore(); var pairing = new RemotePairingCoordinator(new TestRemoteClock(), store);
            var receipt = pairing.RequestPairing(pairing.OpenPairing().Secret, "phone"); pairing.Approve(receipt.RequestId);
            var result = pairing.Poll(receipt.RequestId, receipt.ReceiptSecret); store.Fail = true;
            Assert.ThrowsException<RemoteProtocolException>(() => pairing.Revoke(result.DeviceId));
            Assert.IsFalse(pairing.Authenticate(result.DeviceId, result.Credential));
        }
    }
}
