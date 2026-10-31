using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Interop;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class ComBusyRetryPolicyTests
    {
        [DataTestMethod]
        [DataRow(2,0,10000,false,150)] [DataRow(2,9999,10000,false,150)]
        [DataRow(2,10000,10000,false,-1)] [DataRow(2,0,10000,true,-1)]
        [DataRow(1,0,10000,false,-1)] [DataRow(0,0,10000,false,-1)]
        [DataRow(2,-1,10000,false,-1)] [DataRow(2,0,0,false,-1)]
        public void OnlyServerRetryLaterWithinBudgetCanRetry(int rejection,int elapsed,int budget,bool cancelled,int expected)=>
            Assert.AreEqual(expected,ComBusyRetryPolicy.DelayMilliseconds(rejection,elapsed,budget,cancelled));
    }
}
