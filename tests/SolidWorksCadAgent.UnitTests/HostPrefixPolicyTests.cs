using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Host;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class HostPrefixPolicyTests
    {
        [TestMethod]
        public void Validate_AcceptsExactIpv4LoopbackHttpPrefix()
        {
            var prefix = HostPrefixPolicy.Validate("http://127.0.0.1:53741/");

            Assert.AreEqual("http://127.0.0.1:53741/", prefix.AbsoluteUri);
        }

        [DataTestMethod]
        [DataRow("http://0.0.0.0:53741/")]
        [DataRow("http://+:53741/")]
        [DataRow("http://localhost:53741/")]
        [DataRow("http://192.168.1.20:53741/")]
        [DataRow("https://127.0.0.1:53741/")]
        [DataRow("http://127.0.0.1:53741/api/")]
        [DataRow("http://user:password@127.0.0.1:53741/")]
        [DataRow("http://127.0.0.1/")]
        public void Validate_RejectsAnyPrefixThatCouldExposeOrAlterTheV1Host(string prefix)
        {
            Assert.ThrowsException<ArgumentException>(() => HostPrefixPolicy.Validate(prefix));
        }
    }
}
