using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Host;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class LocalHttpServerTests
    {
        [TestMethod]
        public void Constructor_RejectsNonLoopbackBeforeOpeningAListener()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new LocalHttpServer("http://0.0.0.0:53741/", null));
        }
    }
}
