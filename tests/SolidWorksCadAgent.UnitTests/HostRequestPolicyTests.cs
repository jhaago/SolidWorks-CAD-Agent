using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Host;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class HostRequestPolicyTests
    {
        [TestMethod]
        public void Validate_AllowsNativeJsonPostWithoutBrowserOrigin()
        {
            var result = HostRequestPolicy.Validate("POST", "application/json; charset=utf-8", null, 128, true);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void Validate_RejectsBrowserOriginEvenForLoopbackRequest()
        {
            var result = HostRequestPolicy.Validate("POST", "application/json", "https://example.com", 0, false);

            Assert.AreEqual(403, result.StatusCode);
            StringAssert.Contains(result.JsonBody, "BROWSER_ORIGIN_REJECTED");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("text/plain")]
        [DataRow("application/x-www-form-urlencoded")]
        [DataRow("multipart/form-data")]
        public void Validate_RejectsPostWithoutJsonContentType(string contentType)
        {
            var result = HostRequestPolicy.Validate("POST", contentType, null, 0, false);

            Assert.AreEqual(415, result.StatusCode);
            StringAssert.Contains(result.JsonBody, "JSON_REQUIRED");
        }

        [TestMethod]
        public void Validate_RejectsOversizedBodyBeforeReadingIt()
        {
            var result = HostRequestPolicy.Validate(
                "POST",
                "application/json",
                null,
                HostRequestPolicy.MaximumBodyBytes + 1,
                true);

            Assert.AreEqual(413, result.StatusCode);
            StringAssert.Contains(result.JsonBody, "REQUEST_TOO_LARGE");
        }

        [TestMethod]
        public void Validate_AllowsReadOnlyGetWithoutContentType()
        {
            var result = HostRequestPolicy.Validate("GET", null, null, 0, false);

            Assert.IsNull(result);
        }
    }
}
