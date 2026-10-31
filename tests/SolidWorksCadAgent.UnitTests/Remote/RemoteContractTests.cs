using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Remote;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    [TestClass]
    public class RemoteContractTests
    {
        [TestMethod]
        public void DtoSerializationUsesCamelCase()
        {
            var session = JObject.Parse(JsonConvert.SerializeObject(new RemoteSessionSnapshot { SessionId = "test", AuthorityEpoch = 3 }));
            Assert.AreEqual("test", (string)session["sessionId"]);
            Assert.AreEqual(3, (int)session["authorityEpoch"]);
            Assert.IsNull(session["SessionId"]);
            var frame = JObject.Parse(JsonConvert.SerializeObject(new RemoteCapturedFrame { DisplayGeneration = 7 }));
            Assert.AreEqual(7, (int)frame["displayGeneration"]);
            Assert.IsNotNull(frame["capturedAt"]);
            Assert.IsNull(frame["DisplayGeneration"]);
        }

        private static RemoteInputEvent Click() => new RemoteInputEvent {
            SessionId = "session", AuthorityEpoch = 1, Sequence = 1,
            DisplayGeneration = 1, Kind = "click", Button = "primary", X = .5, Y = .5
        };

        [TestMethod]
        public void InputValidationRejectsInvalidValues()
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -.01, 1.01 }) {
                var input = Click(); input.X = bad; Assert.IsFalse(input.IsValid());
            }
            var value = Click(); Assert.IsTrue(value.IsValid());
            value.Sequence = -1; Assert.IsFalse(value.IsValid());
            value = Click(); value.Kind = "execute"; Assert.IsFalse(value.IsValid());
            value = Click(); value.Button = "unknown"; Assert.IsFalse(value.IsValid());
            value = Click(); value.SessionId = new string('x', 129); Assert.IsFalse(value.IsValid());
            value = Click(); value.Kind = "keyDown"; value.Key = "A"; Assert.IsTrue(value.IsValid());
            value.Key = "DeleteAll"; Assert.IsFalse(value.IsValid());
            value.Key = "Delete"; Assert.IsFalse(value.IsValid());
            value = Click(); value.Kind = "scroll"; value.Scroll = 120; Assert.IsTrue(value.IsValid());
            value.Scroll = int.MaxValue; Assert.IsFalse(value.IsValid());
        }
    }
}
