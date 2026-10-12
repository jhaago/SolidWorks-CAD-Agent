#if NETFRAMEWORK
using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.RemoteAgent.Security;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    [TestClass]
    public class RemoteCredentialStoreTests
    {
        [TestMethod]
        public void DpapiRoundTripDoesNotPersistPlaintextAndCorruptionFailsClosed()
        {
            var directory = Path.Combine(Path.GetTempPath(), "remote-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                var path=Path.Combine(directory,"devices.dat"); var store=new DpapiDeviceCredentialStore(path);
                store.Save(new List<RemoteCredentialRecord> { new RemoteCredentialRecord { DeviceId="device", DeviceName="private label", Verifier="test-verifier" } });
                Assert.AreEqual("private label", store.Load()[0].DeviceName);
                Assert.IsFalse(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("private label"));
                File.WriteAllText(path,"corrupted");
                Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(()=>store.Load());
                Assert.IsFalse(new RemotePairingCoordinator(new TestRemoteClock(),store).Authenticate("device","any"));
            } finally { Directory.Delete(directory,true); }
        }
    }
}
#endif
