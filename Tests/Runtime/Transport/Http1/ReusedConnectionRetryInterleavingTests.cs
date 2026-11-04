using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// #364: the retry after a first-byte timeout on a reused connection, when the platform does not
    /// end the abandoned head read (#293) or ends it only after the socket was disposed (IL2CPP's
    /// I/O selector). The idle connections are real sockets to a silent listener behind a stream
    /// whose read ignores its token and <see cref="Stream.Dispose()"/>, so the head read is always
    /// abandoned by the race (#346), as on IL2CPP.
    /// </summary>
    [TestFixture]
    public class ReusedConnectionRetryInterleavingTests
    {
        private static readonly TimeSpan ShortFirstByte = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan LongSettle = TimeSpan.FromSeconds(30);

        [Test]
        public void TwoIdleConnectionsWithStuckReads_RetryOpensNewSocket_AndNeverUsesTheSecondIdle()
        {
            AssertAsync.Run(async () =>
            {
                using var sink = new SilentListener();
                using var server = new OkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 2, reusedConnectionFirstByteTimeout: ShortFirstByte);
                using var client = CreateClient(new RawSocketTransport(pool));
                var first = await InjectIdleConnectionAsync(pool, sink, server.Port, endReadOnDispose: false);
                var second = await InjectIdleConnectionAsync(pool, sink, server.Port, endReadOnDispose: false);

                // Neither abandoned read ever ends: the retry still completes, after the default
                // settle bound, far inside the request timeout.
                using var response = await client.Get(server.Url("/"))
                    .WithTimeout(TimeSpan.FromSeconds(30))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, server.AcceptCount, "the retry must run on one newly connected socket");
                Assert.Greater(first.BytesWritten, 0, "the first attempt takes the first idle connection");
                Assert.IsTrue(first.IsDisposed, "the silent connection is torn down");
                Assert.AreEqual(0, second.BytesWritten, "the retry must not send on the second idle connection");
                Assert.IsFalse(second.IsDisposed, "the second idle connection is not leased by the retry");
            });
        }

        [Test]
        public void AbandonedReadEndsAfterDispose_RetrySocketOpensOnlyAfterTheReadEnded()
        {
            AssertAsync.Run(async () =>
            {
                using var sink = new SilentListener();
                using var server = new OkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                var transport = new RawSocketTransport(pool) { AbandonedReadSettleTimeout = LongSettle };
                using var client = CreateClient(transport);

                // The read ends (faults) on another thread some time after its socket was disposed,
                // as IL2CPP's I/O selector ends it.
                var idle = await InjectIdleConnectionAsync(pool, sink, server.Port, endReadOnDispose: true);
                server.OnAccept = () => idle.ReadEnded;

                // The settle bound (30 s) is longer than the request timeout: only the end of the
                // abandoned read can let the retry through in time.
                using var response = await client.Get(server.Url("/"))
                    .WithTimeout(TimeSpan.FromSeconds(10))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, server.AcceptCount);
                Assert.AreEqual(
                    new[] { true },
                    server.AcceptObservations,
                    "the retry socket must open only after the abandoned read on the disposed connection ended");
            });
        }

        [Test]
        public void AbandonedReadStillPending_RetryDoesNotOpenASocketBeforeTheSettleBound()
        {
            AssertAsync.Run(async () =>
            {
                using var sink = new SilentListener();
                using var server = new OkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, reusedConnectionFirstByteTimeout: ShortFirstByte);
                var transport = new RawSocketTransport(pool) { AbandonedReadSettleTimeout = LongSettle };
                using var client = CreateClient(transport);
                var idle = await InjectIdleConnectionAsync(pool, sink, server.Port, endReadOnDispose: false);

                // The read never ends and the settle bound outlasts the request: the request times
                // out while it waits, and no retry socket was opened.
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get(server.Url("/"))
                        .WithTimeout(TimeSpan.FromSeconds(1))
                        .SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                Assert.IsTrue(idle.IsDisposed);
                Assert.AreEqual(0, server.AcceptCount, "no retry socket while the abandoned read is pending");
            });
        }

        private static UHttpClient CreateClient(RawSocketTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                AllowCleartext = true,
                Transport = transport,
                DisposeTransport = true
            });
        }

        /// <summary>
        /// Pools a real socket connected to <paramref name="sink"/> under the server's pool key, behind
        /// a <see cref="StuckReadStream"/>.
        /// </summary>
        private static async Task<StuckReadStream> InjectIdleConnectionAsync(
            TcpConnectionPool pool,
            SilentListener sink,
            int serverPort,
            bool endReadOnDispose)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, sink.Port);
            var stream = new StuckReadStream(tcp, endReadOnDispose);
            var connection = new PooledConnection(tcp.Client, stream, "127.0.0.1", serverPort, false);

            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            idle.GetOrAdd($"127.0.0.1:{serverPort}:", _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(connection);
            return stream;
        }

        /// <summary>
        /// A pooled connection's stream whose single read ignores its token and
        /// <see cref="Stream.Dispose()"/>. With <c>endReadOnDispose</c> the read faults on a thread-pool
        /// thread after the stream was disposed; otherwise it never ends. Writes are counted and dropped.
        /// </summary>
        private sealed class StuckReadStream : Stream
        {
            private readonly TaskCompletionSource<int> _read =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TcpClient _tcp;
            private readonly bool _endReadOnDispose;
            private int _disposed;
            private int _readEnded;
            private long _bytesWritten;

            public StuckReadStream(TcpClient tcp, bool endReadOnDispose)
            {
                _tcp = tcp;
                _endReadOnDispose = endReadOnDispose;
            }

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
            public bool ReadEnded => Volatile.Read(ref _readEnded) != 0;
            public long BytesWritten => Interlocked.Read(ref _bytesWritten);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _read.Task;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Interlocked.Add(ref _bytesWritten, count);
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Interlocked.Add(ref _bytesWritten, buffer.Length);
                return default;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Add(ref _bytesWritten, count);
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try { _tcp.Close(); } catch { /* best effort */ }
                    if (_endReadOnDispose)
                    {
                        _ = Task.Run(() =>
                        {
                            Volatile.Write(ref _readEnded, 1);
                            _read.TrySetException(new ObjectDisposedException(nameof(StuckReadStream)));
                        });
                    }
                }

                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>Accepts connections and holds them open without reading or writing.</summary>
        private sealed class SilentListener : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private volatile bool _disposed;

            public SilentListener()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            private async Task AcceptLoopAsync()
            {
                while (!_disposed)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        lock (_lock)
                            _clients.Add(client);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        if (_disposed)
                            return;
                    }
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                        client.Close();
                }
            }
        }

        /// <summary>
        /// Answers every connection with 200 and closes it. <see cref="OnAccept"/> runs as each
        /// connection is accepted; its result is kept in <see cref="AcceptObservations"/>.
        /// </summary>
        private sealed class OkServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly List<bool> _observations = new List<bool>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public OkServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public Func<bool> OnAccept { get; set; }
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int AcceptCount => Volatile.Read(ref _acceptCount);
            public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

            public bool[] AcceptObservations
            {
                get
                {
                    lock (_lock)
                        return _observations.ToArray();
                }
            }

            private async Task AcceptLoopAsync()
            {
                while (!_disposed)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        if (_disposed)
                            return;
                        continue;
                    }

                    var onAccept = OnAccept;
                    lock (_lock)
                    {
                        _clients.Add(client);
                        if (onAccept != null)
                            _observations.Add(onAccept());
                    }

                    Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }

            private static async Task HandleAsync(TcpClient client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[1];
                    var head = new StringBuilder();
                    while (head.Length < 4 || head.ToString(head.Length - 4, 4) != "\r\n\r\n")
                    {
                        if (await stream.ReadAsync(buffer, 0, 1) == 0)
                            return;
                        head.Append((char)buffer[0]);
                    }

                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length);
                    client.Close();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _listener.Stop();
                lock (_lock)
                {
                    foreach (var client in _clients)
                        client.Close();
                }
            }
        }
    }
}
