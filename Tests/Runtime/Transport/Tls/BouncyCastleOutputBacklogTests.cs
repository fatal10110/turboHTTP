using System;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #36 review: records the read path makes the engine queue (alerts, close_notify replies)
    /// cannot be sent while a writer is stalled on the socket. That queue is peer-controlled
    /// (a TLS 1.2 peer can send hello_request records, each answered with a no_renegotiation
    /// warning), so it must stay bounded (CLAUDE.md section 3).
    /// </summary>
    [TestFixture]
    public class BouncyCastleOutputBacklogTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        [Test]
        public void Read_Fails_WhenQueuedOutputExceedsBound_WhileWriterIsStalled()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var sendCloseNotify = new ManualResetEventSlim(false);

            // The SslStream server cannot emit hello_request, so the trigger is its close_notify:
            // the client engine answers it with its own close_notify (RFC 5246 section 7.2.1),
            // which stays queued because the writer holds the gate. The bound is lowered to one
            // byte so that single reply exceeds it.
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                if (!sendCloseNotify.Wait(Bound))
                    return;
                ((SslStream)stream).ShutdownAsync().GetAwaiter().GetResult();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(async () =>
            {
                var inner = new StallableStream(await BouncyCastleLoopbackServer.ConnectAsync(server.Port));
                TlsResult tls;
                using (var handshakeCts = new CancellationTokenSource(Bound))
                {
                    tls = await provider.WrapAsync(inner, "localhost", Array.Empty<string>(), handshakeCts.Token);
                }

                using var secure = tls.SecureStream;
                // The test assembly does not reference the BouncyCastle assembly (it is loaded
                // through TlsProviderSelector), so the internal bound is lowered by reflection.
                var bound = secure.GetType().GetProperty(
                    "MaxPendingOutputBytes", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(bound, "BouncyCastleTlsStream.MaxPendingOutputBytes not found.");
                bound.SetValue(secure, 1);

                var readTask = secure.ReadAsync(new byte[16], 0, 16, CancellationToken.None);
                Assert.IsFalse(readTask.IsCompleted, "Nothing was sent yet; the read must be pending.");

                inner.ArmStall();
                var writeTask = secure.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None);
                await AwaitBounded(inner.StalledWriteEntered, "The writer must reach the stalled socket write.");

                sendCloseNotify.Set();

                var winner = await Task.WhenAny(readTask, Task.Delay(Bound));
                Assert.AreSame(readTask, winner, "The read must fail instead of queueing output forever.");
                try
                {
                    int readResult = await readTask;
                    Assert.Fail($"The read must fail at the bound (it returned {readResult}).");
                }
                catch (IOException ex)
                {
                    StringAssert.Contains("queued output", ex.Message);
                }
                Assert.IsTrue(inner.IsDisposed, "The connection must be aborted at the bound.");

                winner = await Task.WhenAny(writeTask, Task.Delay(Bound));
                Assert.AreSame(writeTask, winner, "Aborting must also release the stalled writer.");
                Assert.IsTrue(writeTask.IsFaulted, "The stalled write must fail once the connection is aborted.");
            });
        }

        private static async Task AwaitBounded(Task task, string because)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.AreSame(task, winner, because);
        }

        /// <summary>Passes through to the socket until armed; then writes never complete until disposal.</summary>
        private sealed class StallableStream : Stream
        {
            private readonly Stream _inner;
            private readonly TaskCompletionSource<bool> _stalledWrite =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stalledWriteEntered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _armed;
            private int _disposed;

            public StallableStream(Stream inner)
            {
                _inner = inner;
            }

            public Task StalledWriteEntered => _stalledWriteEntered.Task;

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void ArmStall() => Volatile.Write(ref _armed, 1);

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
                    throw new InvalidOperationException("The test only stalls asynchronous writes.");
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Volatile.Read(ref _armed) == 0)
                    return _inner.WriteAsync(buffer, offset, count, cancellationToken);

                _stalledWriteEntered.TrySetResult(true);
                return _stalledWrite.Task;
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _stalledWrite.TrySetException(new ObjectDisposedException(nameof(StallableStream)));
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
