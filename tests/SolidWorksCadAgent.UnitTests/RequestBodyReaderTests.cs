using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Host;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class RequestBodyReaderTests
    {
        [TestMethod]
        public async Task ReadAsync_AbortsAReadThatDoesNotCompleteBeforeDeadline()
        {
            var aborted = false;

            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                RequestBodyReader.ReadAsync(
                    new NeverCompletingStream(),
                    Encoding.UTF8,
                    1024,
                    TimeSpan.FromMilliseconds(20),
                    () => aborted = true,
                    CancellationToken.None));

            Assert.IsTrue(aborted);
        }

        [TestMethod]
        public async Task ReadAsync_ReturnsBodyThatCompletesWithinDeadline()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"prompt\":\"plate\"}");
            using (var stream = new MemoryStream(bytes))
            {
                var body = await RequestBodyReader.ReadAsync(
                    stream,
                    Encoding.UTF8,
                    1024,
                    TimeSpan.FromSeconds(1),
                    () => Assert.Fail("Completed read must not abort."),
                    CancellationToken.None);

                Assert.AreEqual("{\"prompt\":\"plate\"}", body);
            }
        }

        private sealed class NeverCompletingStream : Stream
        {
            private readonly TaskCompletionSource<int> _pending = new TaskCompletionSource<int>();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _pending.Task;
            }
        }
    }
}
