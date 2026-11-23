using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>#1272: the no-progress watchdog reads to the end and still fails a stalled transfer.</summary>
    [TestFixture]
    public class ProgressWatchdogTests
    {
        [Test]
        public void ReadToEnd_CountsEveryByte()
        {
            AssertAsync.Run(async () =>
            {
                using var watchdog = new ProgressWatchdog(TimeSpan.FromSeconds(30));
                using var source = new MemoryStream(new byte[300001]);

                long received = await watchdog.ReadToEndAsync(source, new byte[64 * 1024]).ConfigureAwait(false);

                Assert.AreEqual(300001, received);
                Assert.IsFalse(watchdog.Expired);
            });
        }

        [Test]
        public void ReadToEnd_StalledSource_FailsWithTheBytesReceived()
        {
            using var watchdog = new ProgressWatchdog(TimeSpan.FromMilliseconds(100));
            using var source = new StallingStream(bytesBeforeStall: 10);

            var ex = AssertAsync.ThrowsAsync<TimeoutException>(
                () => watchdog.ReadToEndAsync(source, new byte[64]), TimeSpan.FromSeconds(30));

            Assert.IsTrue(watchdog.Expired);
            StringAssert.Contains("after 10 bytes", ex.Message);
        }

        /// <summary>Returns a few bytes, then never completes a read until it is cancelled.</summary>
        private sealed class StallingStream : Stream
        {
            private int _remaining;

            public StallingStream(int bytesBeforeStall)
            {
                _remaining = bytesBeforeStall;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_remaining > 0)
                {
                    int count = Math.Min(_remaining, buffer.Length);
                    _remaining -= count;
                    return count;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
