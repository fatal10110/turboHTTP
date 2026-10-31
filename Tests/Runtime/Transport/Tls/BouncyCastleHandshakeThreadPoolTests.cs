using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #169: the BouncyCastle handshake (and later I/O) never blocks a thread on the inner stream.
    /// Over <see cref="SaeaStream"/> a synchronous Read/Write is sync-over-async that needs a
    /// second pool thread to complete; with the pool clamped, blocking handshakes stall.
    /// </summary>
    // ThreadPool limits are process-global; Unity's runner executes tests serially.
    [TestFixture]
    public class BouncyCastleHandshakeThreadPoolTests
    {
        /// <summary>Forwards to the inner stream and counts synchronous I/O calls.</summary>
        private sealed class SyncIoSpyStream : Stream
        {
            private readonly Stream _inner;
            private int _syncReads;
            private int _syncWrites;

            public SyncIoSpyStream(Stream inner) => _inner = inner;

            public int SyncReads => Volatile.Read(ref _syncReads);
            public int SyncWrites => Volatile.Read(ref _syncWrites);

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref _syncReads);
                return _inner.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref _syncWrites);
                _inner.Write(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                _inner.ReadAsync(buffer, offset, count, ct);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                _inner.ReadAsync(buffer, ct);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                _inner.WriteAsync(buffer, offset, count, ct);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
                _inner.WriteAsync(buffer, ct);

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        [Test]
        public void ConcurrentHandshakes_OverSaea_WithClampedThreadPool_CompleteWithoutBlockingIo()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrIgnore();

            // Echo one byte so each connection also proves post-handshake I/O.
            using var server = BouncyCastleLoopbackServer.TryStartTls(stream =>
            {
                var one = new byte[1];
                BouncyCastleLoopbackServer.ReadExact(stream, one, 1);
                stream.Write(one, 0, 1);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
            if (server == null)
            {
                Assert.Ignore("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
            ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
            // The smallest pool the runtime accepts (it refuses fewer workers than CPUs).
            int clampWorkers = Math.Max(Environment.ProcessorCount, minWorkers);
            int clampIo = Math.Max(Environment.ProcessorCount, minIo);
            // More handshakes than pool threads (and than the former 4-permit handshake limit).
            int handshakes = Math.Max(8, clampWorkers * 2);

            var spies = new ConcurrentBag<SyncIoSpyStream>();
            var sockets = new ConcurrentBag<Socket>();
            bool clamped = ThreadPool.SetMaxThreads(clampWorkers, clampIo);
            try
            {
                var work = Task.Run(async () =>
                {
                    var connections = new Task[handshakes];
                    for (int i = 0; i < handshakes; i++)
                    {
                        connections[i] = HandshakeAndEchoAsync(provider, server.Port, (byte)i, spies, sockets);
                    }
                    await Task.WhenAll(connections).ConfigureAwait(false);
                });

                // Wait on this thread with a kernel wait handle: a starved pool cannot fire timers.
                bool finished = ((IAsyncResult)work).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60));
                if (!finished)
                {
                    foreach (var socket in sockets)
                    {
                        try { socket.Dispose(); } catch { }
                    }
                    Assert.Fail($"{handshakes} concurrent BouncyCastle handshakes over SAEA stalled with the " +
                                $"thread pool clamped to {clampWorkers} workers (clamp applied: {clamped}).");
                }

                Assert.IsNull(work.Exception, work.Exception?.ToString());
            }
            finally
            {
                ThreadPool.SetMaxThreads(maxWorkers, maxIo);
                foreach (var socket in sockets)
                {
                    try { socket.Dispose(); } catch { }
                }
            }

            Assert.AreEqual(handshakes, spies.Count);
            foreach (var spy in spies)
            {
                Assert.AreEqual(0, spy.SyncReads, "Synchronous Read on the inner SAEA stream blocks a pool thread.");
                Assert.AreEqual(0, spy.SyncWrites, "Synchronous Write on the inner SAEA stream blocks a pool thread.");
            }
            Assert.IsNull(server.HandlerError, server.HandlerError?.ToString());
        }

        private static async Task HandshakeAndEchoAsync(
            ITlsProvider provider,
            int port,
            byte value,
            ConcurrentBag<SyncIoSpyStream> spies,
            ConcurrentBag<Socket> sockets)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sockets.Add(socket);
            await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

            var spy = new SyncIoSpyStream(new SaeaStream(new SaeaSocketChannel(socket)));
            spies.Add(spy);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var tls = await provider.WrapAsync(spy, "localhost", Array.Empty<string>(), cts.Token)
                .ConfigureAwait(false);
            var secure = tls.SecureStream;

            await secure.WriteAsync(new[] { value }, 0, 1, cts.Token).ConfigureAwait(false);
            var echo = new byte[1];
            int read = await secure.ReadAsync(echo, 0, 1, cts.Token).ConfigureAwait(false);
            Assert.AreEqual(1, read);
            Assert.AreEqual(value, echo[0]);

            // Counts are asserted after the run; Dispose's best-effort close_notify is synchronous
            // by contract (Stream.Dispose), so the stream is released by the socket cleanup instead.
        }
    }
}
