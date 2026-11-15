using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #910: disposing the BouncyCastle TLS stream never blocks on an inner write. close_notify
    /// (RFC 8446 section 6.1) goes out through the inner stream's async write; a write that does
    /// not finish inline owns the inner stream until it ends or a short bound passes, and the
    /// inner stream is disposed exactly once on every path.
    /// </summary>
    [TestFixture]
    public class BouncyCastleDisposeTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        [Test]
        public void Dispose_WithStalledCloseNotifyWrite_ReturnsWithoutBlocking_AndDisposesInnerOnceAfterBound()
        {
            RunWithServer(async (provider, port) =>
            {
                var (secure, inner) = await HandshakeAsync(provider, port);
                inner.ArmStall();

                secure.Dispose(); // used to write close_notify synchronously before returning

                Assert.AreEqual(0, inner.SyncWrites, "Dispose must not write synchronously on the inner stream.");
                Assert.AreEqual(1, inner.AsyncWrites, "Dispose must start the close_notify write asynchronously.");
                Assert.IsFalse(inner.IsDisposed, "The pending close_notify write owns the inner stream until it ends or the bound passes.");

                await AwaitBounded(inner.Disposed, "The hand-off must dispose the inner stream at the bound.");
                secure.Dispose();
                Assert.AreEqual(1, inner.DisposeCalls, "The inner stream must be disposed exactly once.");
            });
        }

        [Test]
        public void Dispose_WithWorkingSocket_SendsCloseNotifyAsynchronously_AndDisposesInnerOnce()
        {
            RunWithServer(async (provider, port) =>
            {
                var (secure, inner) = await HandshakeAsync(provider, port);

                secure.Dispose();

                await AwaitBounded(inner.Disposed, "The inner stream must be disposed once close_notify was written.");
                Assert.AreEqual(0, inner.SyncWrites, "Dispose must not write synchronously on the inner stream.");
                Assert.AreEqual(1, inner.AsyncWrites, "close_notify must be written through the async API.");
                secure.Dispose();
                Assert.AreEqual(1, inner.DisposeCalls, "The inner stream must be disposed exactly once.");
            });
        }

        [Test]
        public void DisposeAsync_AwaitsCloseNotifyWrite_ThenDisposesInnerOnce()
        {
            RunWithServer(async (provider, port) =>
            {
                var (secure, inner) = await HandshakeAsync(provider, port);

                await secure.DisposeAsync();

                Assert.IsTrue(inner.IsDisposed, "DisposeAsync must dispose the inner stream before it completes.");
                Assert.AreEqual(0, inner.SyncWrites, "DisposeAsync must not write synchronously on the inner stream.");
                Assert.AreEqual(1, inner.AsyncWrites, "DisposeAsync must write close_notify.");
                secure.Dispose();
                await secure.DisposeAsync();
                Assert.AreEqual(1, inner.DisposeCalls, "The inner stream must be disposed exactly once.");
            });
        }

        [Test]
        public void DisposeAsync_WithStalledCloseNotifyWrite_CompletesAtBound_AndDisposesInnerOnce()
        {
            RunWithServer(async (provider, port) =>
            {
                var (secure, inner) = await HandshakeAsync(provider, port);
                inner.ArmStall();

                var dispose = secure.DisposeAsync().AsTask();
                await AwaitBounded(dispose, "DisposeAsync must give up on a stalled close_notify write at the bound.");

                Assert.IsTrue(inner.IsDisposed, "DisposeAsync must dispose the inner stream before it completes.");
                Assert.AreEqual(1, inner.AsyncWrites);
                Assert.AreEqual(0, inner.SyncWrites);
                Assert.AreEqual(1, inner.DisposeCalls, "The inner stream must be disposed exactly once.");
            });
        }

        [Test]
        public void Dispose_WhileWriterHoldsTheRecordStream_ClosesAsIs_AndFailsTheWriter()
        {
            RunWithServer(async (provider, port) =>
            {
                var (secure, inner) = await HandshakeAsync(provider, port);
                inner.ArmStall();
                var write = secure.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None);
                await AwaitBounded(inner.StalledWriteEntered, "The writer must reach the stalled socket write.");

                secure.Dispose();

                Assert.IsTrue(inner.IsDisposed, "With the record stream owned by a writer, the socket is closed at once.");
                Assert.AreEqual(1, inner.AsyncWrites, "No close_notify may be interleaved with the writer's record.");
                Assert.AreEqual(0, inner.SyncWrites);
                await AwaitBounded(write, "Disposal must release the stalled writer.");
                Assert.IsTrue(write.IsFaulted, "The stalled write must fail once the connection is closed.");
                Assert.AreEqual(1, inner.DisposeCalls, "The inner stream must be disposed exactly once.");
            });
        }

        private static void RunWithServer(Func<ITlsProvider, int, Task> body)
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = BouncyCastleLoopbackServer.TryStartTls(BouncyCastleLoopbackServer.DrainUntilClosed);
            if (server == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            AssertAsync.Run(() => body(provider, server.Port));
        }

        private static async Task<(Stream Secure, ProbeStream Inner)> HandshakeAsync(ITlsProvider provider, int port)
        {
            var inner = new ProbeStream(await BouncyCastleLoopbackServer.ConnectAsync(port));
            using var handshakeCts = new CancellationTokenSource(Bound);
            var tls = await provider.WrapAsync(inner, "localhost", Array.Empty<string>(), handshakeCts.Token);
            inner.ResetCounts();
            return (tls.SecureStream, inner);
        }

        private static async Task AwaitBounded(Task task, string because)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Bound));
            Assert.AreSame(task, winner, because);
        }

        /// <summary>
        /// Counts sync/async writes and Dispose calls. Once armed, async writes never complete
        /// until disposal (a peer that stopped reading with a full socket buffer).
        /// </summary>
        private sealed class ProbeStream : Stream
        {
            private readonly Stream _inner;
            private readonly TaskCompletionSource<bool> _stalledWrite =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stalledWriteEntered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _disposed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _armed;
            private int _syncWrites;
            private int _asyncWrites;
            private int _disposeCalls;

            public ProbeStream(Stream inner)
            {
                _inner = inner;
            }

            public Task StalledWriteEntered => _stalledWriteEntered.Task;
            public Task Disposed => _disposed.Task;
            public bool IsDisposed => _disposed.Task.IsCompleted;
            public int SyncWrites => Volatile.Read(ref _syncWrites);
            public int AsyncWrites => Volatile.Read(ref _asyncWrites);
            public int DisposeCalls => Volatile.Read(ref _disposeCalls);

            public void ArmStall() => Volatile.Write(ref _armed, 1);

            public void ResetCounts()
            {
                Volatile.Write(ref _syncWrites, 0);
                Volatile.Write(ref _asyncWrites, 0);
            }

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
                Interlocked.Increment(ref _syncWrites);
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _asyncWrites);
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
                if (disposing)
                {
                    // Counted on every call: the TLS stream must call it exactly once.
                    if (Interlocked.Increment(ref _disposeCalls) == 1)
                    {
                        _stalledWrite.TrySetException(new ObjectDisposedException(nameof(ProbeStream)));
                        _inner.Dispose();
                        _disposed.TrySetResult(true);
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}
