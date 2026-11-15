using System;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #912: a record the read path sends on the peer's behalf (the close_notify reply to the
    /// peer's close_notify) is best effort. When its write fails, the read still returns the
    /// decrypted data and the clean end of stream (RFC 8446 section 6.1), and the write path
    /// reports the dead socket on its own call.
    /// </summary>
    [TestFixture]
    public class BouncyCastleReplyFlushTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        [Test]
        public void Read_DeliversDataAndEndOfStream_WhenCloseNotifyReplyWriteFails()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            var payload = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");

            // Response then close_notify. The client engine only processes them when the client
            // reads, which happens after the failing write is armed.
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.SendCloseNotify((SslStream)stream);
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var inner = new FailingWriteStream(await BouncyCastleLoopbackServer.ConnectAsync(server.Port));
                using var secure = await HandshakeAsync(provider, inner);
                inner.ArmFailure();

                var received = new MemoryStream();
                var buffer = new byte[256];
                while (true)
                {
                    var readTask = secure.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    var winner = await Task.WhenAny(readTask, Task.Delay(Bound));
                    Assert.AreSame(readTask, winner, "The read must complete once the server's records arrived.");
                    int read = await readTask; // threw the reply's write failure before #912
                    if (read == 0)
                        break;
                    received.Write(buffer, 0, read);
                }

                CollectionAssert.AreEqual(payload, received.ToArray(), "Every decrypted byte must be delivered.");
                Assert.Greater(inner.FailedWrites, 0, "The close_notify reply must have been attempted on the failing socket.");

                try
                {
                    await secure.WriteAsync(new byte[] { 1 }, 0, 1, CancellationToken.None);
                    Assert.Fail("A write after the failed reply must report the dead connection.");
                }
                catch (IOException)
                {
                }
            });
        }

        [Test]
        public void Write_AfterFailedRecordWrite_KeepsFailing_EvenWhenTheSocketAcceptsBytesAgain()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = BouncyCastleLoopbackServer.TryStartTls(BouncyCastleLoopbackServer.DrainUntilClosed);
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var inner = new FailingWriteStream(await BouncyCastleLoopbackServer.ConnectAsync(server.Port));
                using var secure = await HandshakeAsync(provider, inner);

                inner.ArmFailure();
                try
                {
                    await secure.WriteAsync(new byte[] { 1 }, 0, 1, CancellationToken.None);
                    Assert.Fail("The armed write must fail.");
                }
                catch (IOException)
                {
                }

                // The failed record's ciphertext is gone; a later record would leave a gap in the
                // record sequence. The connection must stay unable to send.
                inner.Disarm();
                int writesBefore = inner.PassedWrites;
                try
                {
                    await secure.WriteAsync(new byte[] { 2 }, 0, 1, CancellationToken.None);
                    Assert.Fail("A write after a failed record write must fail too.");
                }
                catch (IOException)
                {
                }

                Assert.AreEqual(writesBefore, inner.PassedWrites, "No record may follow a lost one.");
            });
        }

        private static async Task<Stream> HandshakeAsync(ITlsProvider provider, Stream inner)
        {
            using var handshakeCts = new CancellationTokenSource(Bound);
            var tls = await provider.WrapAsync(inner, "localhost", Array.Empty<string>(), handshakeCts.Token);
            return tls.SecureStream;
        }

        /// <summary>Passes through to the socket until armed; then every write fails like EPIPE.</summary>
        private sealed class FailingWriteStream : Stream
        {
            private readonly Stream _inner;
            private int _armed;
            private int _failedWrites;
            private int _passedWrites;

            public FailingWriteStream(Stream inner)
            {
                _inner = inner;
            }

            public int FailedWrites => Volatile.Read(ref _failedWrites);

            public int PassedWrites => Volatile.Read(ref _passedWrites);

            public void ArmFailure() => Volatile.Write(ref _armed, 1);

            public void Disarm() => Volatile.Write(ref _armed, 0);

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Volatile.Read(ref _armed) != 0)
                {
                    Interlocked.Increment(ref _failedWrites);
                    throw new IOException("Simulated broken pipe.");
                }

                Interlocked.Increment(ref _passedWrites);
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Volatile.Read(ref _armed) != 0)
                {
                    Interlocked.Increment(ref _failedWrites);
                    return Task.FromException(new IOException("Simulated broken pipe."));
                }

                Interlocked.Increment(ref _passedWrites);
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
