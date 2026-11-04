using System;
using System.Buffers;
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
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Http1
{
    /// <summary>
    /// Connection-reuse safety of the HTTP/1.1 transport: an incomplete request or a switched
    /// protocol never leaves a connection in the pool, and stale-connection replay follows
    /// RFC 9110 sec 9.2.2 / RFC 9112 sec 9.3.1.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportConnectionReuseTests
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        // --- #37: early final response with an unsent body ---

        [Test]
        public void ExpectContinue_EarlyFinalWithUnsentBody_DiscardingServerNeverReceivesNextRequestAsBody()
        {
            AssertAsync.Run(async () =>
            {
                const string body = "0123456789abcdef";
                var discarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                string followUpLine = null;

                using var server = new LoopbackServer(async (stream, index) =>
                {
                    if (index == 1)
                    {
                        await ReadHeadAsync(stream);
                        await WriteAsync(stream,
                            "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                        // RFC 9110 sec 10.1.1: the server may read and discard the declared body.
                        var consumed = await ReadUpToAsync(stream, body.Length);
                        discarded.TrySetResult(Encoding.ASCII.GetString(consumed));
                        if (consumed.Length > 0)
                        {
                            await WriteAsync(stream,
                                "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }

                        return;
                    }

                    var head = await ReadHeadAsync(stream);
                    followUpLine = FirstLine(head);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                });

                using var client = CreateClient(new RawSocketTransport(
                    pool: null,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: new StreamingOptions { ExpectContinueTimeoutMs = 30000 }));

                using (var rejected = await client.Post(server.Url("/reject"))
                           .WithBody(body)
                           .WithExpectContinue()
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.ExpectationFailed, rejected.StatusCode);
                }

                using var followUp = await client.Get(server.Url("/follow-up")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, followUp.StatusCode);
                Assert.AreEqual("GET /follow-up HTTP/1.1", followUpLine);
                Assert.AreEqual(string.Empty, await WithGuard(discarded.Task),
                    "The rejected connection must be closed, not reused for the next request.");
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void ExpectContinue_Expect417_ReplayableBody_ReturnedToCallerWithoutRetry()
        {
            AssertAsync.Run(async () =>
            {
                string firstHead = null;
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    var head = await ReadHeadAsync(stream);
                    if (index == 1)
                    {
                        firstHead = head;
                        await WriteAsync(stream,
                            "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                        return;
                    }

                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                using var client = CreateClient(new RawSocketTransport(
                    pool: null,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: new StreamingOptions { ExpectContinueTimeoutMs = 30000 }));

                using var response = await client.Post(server.Url("/upload"))
                    .WithBody("replayable")
                    .WithExpectContinue()
                    .SendBufferedAsync();

                // Phase 22b.1: no automatic retry without Expect; the 417 reaches the caller.
                Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
                StringAssert.Contains("Expect: 100-continue", firstHead);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        [Test]
        public void ExpectContinue_ZeroLengthBody_ConnectionStaysReusable()
        {
            AssertAsync.Run(async () =>
            {
                var requestLines = new List<string>();
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    var first = FirstLine(await ReadHeadAsync(stream));
                    lock (requestLines)
                        requestLines.Add(first);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                    var second = await TryReadHeadAsync(stream);
                    if (second == null)
                        return;

                    lock (requestLines)
                        requestLines.Add(FirstLine(second));
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                using var client = CreateClient(new RawSocketTransport());

                using (var first = await client.Post(server.Url("/empty"))
                           .WithBody(Array.Empty<byte>())
                           .WithExpectContinue()
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                }

                using var second = await client.Get(server.Url("/next")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual(1, server.AcceptCount);
                CollectionAssert.AreEqual(
                    new[] { "POST /empty HTTP/1.1", "GET /next HTTP/1.1" },
                    requestLines);
            });
        }

        // --- #109: send failure vs early final response ---

        [Test]
        public void ExpectContinue_BodySendFailure_ErrorFinalResponse_ReturnedAndConnectionNotReused()
        {
            AssertAsync.Run(async () =>
            {
                const int bytesBeforeFailure = 5;
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    if (index == 1)
                    {
                        await ReadHeadAsync(stream);
                        await WriteAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                        await ReadUpToAsync(stream, bytesBeforeFailure);
                        await WriteAsync(stream,
                            "HTTP/1.1 413 Payload Too Large\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

                        if (await TryReadHeadAsync(stream) != null)
                        {
                            await WriteAsync(stream,
                                "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }

                        return;
                    }

                    await ReadHeadAsync(stream);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                using var body = new FailAfterReadStream(Encoding.ASCII.GetBytes("abcdefghij"), bytesBeforeFailure);
                using var client = CreateClient(new RawSocketTransport(
                    pool: null,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: new StreamingOptions { ExpectContinueTimeoutMs = 30000 }));

                using (var rejected = await client.Post(server.Url("/upload"))
                           .WithStreamBody(body, body.Length, leaveOpen: true)
                           .WithExpectContinue()
                           .SendBufferedAsync())
                {
                    Assert.AreEqual((HttpStatusCode)413, rejected.StatusCode);
                }

                using var next = await client.Get(server.Url("/next")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        // --- #45: cancellation during the expect-continue wait ---

        [Test]
        public void ExpectContinue_CancelDuringWait_ReadBufferReturnedOnlyAfterPendingReadStops()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer((stream, index) => Task.CompletedTask, keepFirstConnectionOpen: true);
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(
                    pool,
                    tlsBackend: TlsBackend.Auto,
                    http2Options: new Http2Options(),
                    streamingOptions: new StreamingOptions { ExpectContinueTimeoutMs = 30000 }));

                var pendingStream = await InjectIdleConnectionAsync(
                    pool,
                    server.Port,
                    tcp => new PendingReadStream(tcp));

                using var cts = new CancellationTokenSource();
                var send = client.Post(server.Url("/upload"))
                    .WithBody("payload")
                    .WithExpectContinue()
                    .SendBufferedAsync(cts.Token)
                    .AsTask();

                await WithGuard(pendingStream.ReadStarted);
                cts.Cancel();

                Assert.AreSame(send, await Task.WhenAny(send, Task.Delay(Guard)), "The cancelled send must complete.");
                Assert.IsTrue(send.IsFaulted || send.IsCanceled, "The cancelled send must not succeed.");
                _ = send.Exception;

                Assert.IsTrue(pendingStream.Disposed, "Cancellation must close the connection.");
                Assert.IsFalse(pendingStream.BufferReturnedWhileReadPending,
                    "The pooled read buffer was returned while a read could still write into it.");
            });
        }

        // --- #43: 101 Switching Protocols ---

        [Test]
        public void SwitchingProtocols101_ConnectionNeverReturnedToPool()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                    if (index == 1)
                    {
                        await WriteAsync(stream,
                            "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n");

                        if (await TryReadHeadAsync(stream) != null)
                        {
                            await WriteAsync(stream,
                                "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }

                        return;
                    }

                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                });

                using var client = CreateClient(new RawSocketTransport());

                using (var upgraded = await client.Get(server.Url("/upgrade"))
                           .WithHeader("Connection", "Upgrade")
                           .WithHeader("Upgrade", "websocket")
                           .SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.SwitchingProtocols, upgraded.StatusCode);
                }

                using var next = await client.Get(server.Url("/next")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void Parse_101SwitchingProtocols_IsNeverKeepAlive()
        {
            AssertAsync.Run(async () =>
            {
                var bytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nConnection: upgrade, keep-alive\r\nUpgrade: websocket\r\n\r\n");
                using var ms = new MemoryStream(bytes);
                using var head = await Http11ResponseParser.ParseHeadAsync(ms, HttpMethod.GET, CancellationToken.None);

                Assert.AreEqual(HttpStatusCode.SwitchingProtocols, head.StatusCode);
                Assert.IsFalse(head.KeepAlive);
            });
        }

        // --- #42: EOF before the status line on a reused connection ---

        [Test]
        public void StaleConnection_EofBeforeStatusLine_IdempotentRequestRetried()
        {
            AssertAsync.Run(async () =>
            {
                using var server = CreateIdleThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                var stale = await InjectIdleConnectionAsync(
                    pool,
                    server.Port,
                    tcp => new ScriptedReadStream(tcp, readFailure: null));

                using var response = await client.Get(server.Url("/")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.Greater(stale.BytesWritten, 0, "The request must have reached the stale connection first.");
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        [Test]
        public void FreshConnection_EofBeforeStatusLine_SurfacesError()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new LoopbackServer(async (stream, index) =>
                {
                    await ReadHeadAsync(stream);
                    // Close without any response byte.
                });

                using var client = CreateClient(new RawSocketTransport());

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Get(server.Url("/")).SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreEqual(1, server.AcceptCount);
            });
        }

        // --- #48: bodiless non-idempotent request after its head was fully written ---

        [Test]
        public void StaleConnection_BodilessPost_ResetAfterRequestWritten_NotReplayed()
        {
            AssertAsync.Run(async () =>
            {
                using var server = CreateIdleThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                var stale = await InjectIdleConnectionAsync(
                    pool,
                    server.Port,
                    tcp => new ScriptedReadStream(tcp, () => new IOException("Connection reset by peer")));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await client.Post(server.Url("/charge")).SendBufferedAsync());

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.Greater(stale.BytesWritten, 0);
                Assert.AreEqual(1, server.AcceptCount, "A fully written POST must not be replayed.");
            });
        }

        [Test]
        public void StaleConnection_Get_ResetAfterRequestWritten_Retried()
        {
            AssertAsync.Run(async () =>
            {
                using var server = CreateIdleThenOkServer();
                var pool = new TcpConnectionPool(maxConnectionsPerHost: 1);
                using var client = CreateClient(new RawSocketTransport(pool));
                await InjectIdleConnectionAsync(
                    pool,
                    server.Port,
                    tcp => new ScriptedReadStream(tcp, () => new IOException("Connection reset by peer")));

                using var response = await client.Get(server.Url("/")).SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, server.AcceptCount);
            });
        }

        // --- helpers ---

        private static UHttpClient CreateClient(RawSocketTransport transport)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });
        }

        /// <summary>Connection 1 stays idle (it backs the injected stale connection); later ones answer 200.</summary>
        private static LoopbackServer CreateIdleThenOkServer()
        {
            return new LoopbackServer(async (stream, index) =>
            {
                if (index == 1)
                    return;

                await ReadHeadAsync(stream);
                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }, keepFirstConnectionOpen: true);
        }

        private static async Task<T> InjectIdleConnectionAsync<T>(
            TcpConnectionPool pool,
            int port,
            Func<TcpClient, T> createStream)
            where T : Stream
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = createStream(tcp);
            var connection = new PooledConnection(tcp.Client, stream, "127.0.0.1", port, false);

            var idleField = typeof(TcpConnectionPool).GetField(
                "_idleConnections",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);
            idle.GetOrAdd($"127.0.0.1:{port}:", _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(connection);
            return stream;
        }

        private static async Task WithGuard(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(Guard)) != task)
                Assert.Fail("Operation did not complete within the test guard.");
            await task;
        }

        private static async Task<T> WithGuard<T>(Task<T> task)
        {
            await WithGuard((Task)task);
            return await task;
        }

        private static string FirstLine(string head)
        {
            int end = head.IndexOf("\r\n", StringComparison.Ordinal);
            return end < 0 ? head : head.Substring(0, end);
        }

        private static async Task<string> ReadHeadAsync(Stream stream)
        {
            var head = await TryReadHeadAsync(stream);
            if (head == null)
                throw new IOException("Connection closed before a request head.");
            return head;
        }

        /// <summary>Reads one request head byte by byte; null on EOF or reset before its end.</summary>
        private static async Task<string> TryReadHeadAsync(Stream stream)
        {
            var bytes = new List<byte>(256);
            var one = new byte[1];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(one, 0, 1);
                }
                catch (IOException)
                {
                    return null;
                }

                if (read == 0)
                    return null;

                bytes.Add(one[0]);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes.ToArray());
                if (n > 16 * 1024)
                    throw new IOException("Request head too large.");
            }
        }

        /// <summary>Reads until <paramref name="count"/> bytes arrived or the peer closed.</summary>
        private static async Task<byte[]> ReadUpToAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, total, count - total);
                }
                catch (IOException)
                {
                    break;
                }

                if (read == 0)
                    break;
                total += read;
            }

            Array.Resize(ref buffer, total);
            return buffer;
        }

        private static async Task WriteAsync(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private sealed class LoopbackServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Func<NetworkStream, int, Task> _handler;
            private readonly bool _keepFirstConnectionOpen;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _lock = new object();
            private int _acceptCount;
            private volatile bool _disposed;

            public LoopbackServer(Func<NetworkStream, int, Task> handler, bool keepFirstConnectionOpen = false)
            {
                _handler = handler;
                _keepFirstConnectionOpen = keepFirstConnectionOpen;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _ = AcceptLoopAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public int AcceptCount => Volatile.Read(ref _acceptCount);

            public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

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

                    lock (_lock)
                        _clients.Add(client);

                    int index = Interlocked.Increment(ref _acceptCount);
                    _ = Task.Run(() => HandleAsync(client, index));
                }
            }

            private async Task HandleAsync(TcpClient client, int index)
            {
                bool keepOpen = _keepFirstConnectionOpen && index == 1;
                try
                {
                    await _handler(client.GetStream(), index);
                }
                catch (IOException)
                {
                    // The client closed the connection; the assertions observe the outcome.
                }
                catch (ObjectDisposedException)
                {
                    // Server disposed while the handler ran.
                }
                finally
                {
                    if (!keepOpen)
                        client.Close();
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
                    _clients.Clear();
                }
            }
        }

        /// <summary>
        /// Accepts every write; reads either return EOF (<paramref name="readFailure"/> null: an idle
        /// connection the server closed) or throw (a reset after the server read the request).
        /// </summary>
        private sealed class ScriptedReadStream : Stream
        {
            private readonly TcpClient _tcp;
            private readonly Func<Exception> _readFailure;
            private long _bytesWritten;

            public ScriptedReadStream(TcpClient tcp, Func<Exception> readFailure)
            {
                _tcp = tcp;
                _readFailure = readFailure;
            }

            public long BytesWritten => Interlocked.Read(ref _bytesWritten);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_readFailure != null)
                    throw _readFailure();
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _readFailure != null
                    ? Task.FromException<int>(_readFailure())
                    : Task.FromResult(0);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Add(ref _bytesWritten, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Interlocked.Add(ref _bytesWritten, count);
                return Task.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _tcp.Close();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// A read that ignores its cancellation token (as some platform socket streams do once a
        /// read started) and completes only when the stream is closed. On close it checks whether
        /// the buffer it was handed is already back in <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        private sealed class PendingReadStream : Stream
        {
            private readonly TcpClient _tcp;
            private readonly TaskCompletionSource<bool> _readStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<int> _pendingRead =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[] _readBuffer;
            private int _disposed;

            public PendingReadStream(TcpClient tcp)
            {
                _tcp = tcp;
            }

            public Task ReadStarted => _readStarted.Task;

            public bool Disposed => Volatile.Read(ref _disposed) != 0;

            public bool BufferReturnedWhileReadPending { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Volatile.Write(ref _readBuffer, buffer);
                _readStarted.TrySetResult(true);
                return _pendingRead.Task;
            }

            public override void Write(byte[] buffer, int offset, int count) { }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    var readBuffer = Volatile.Read(ref _readBuffer);
                    if (readBuffer != null)
                    {
                        // ArrayPool hands a just-returned array back to the next renter of that size;
                        // getting the read buffer here means the reader released it too early.
                        var probe = ArrayPool<byte>.Shared.Rent(readBuffer.Length);
                        BufferReturnedWhileReadPending = ReferenceEquals(probe, readBuffer);
                        ArrayPool<byte>.Shared.Return(probe);
                    }

                    _pendingRead.TrySetException(new IOException("Connection closed"));
                    _tcp.Close();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>A non-seekable body source that fails after yielding a fixed number of bytes.</summary>
        private sealed class FailAfterReadStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _failAfter;
            private int _position;

            public FailAfterReadStream(byte[] data, int failAfter)
            {
                _data = data;
                _failAfter = failAfter;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _failAfter)
                    throw new IOException("body-source-failure");

                int n = Math.Min(count, _failAfter - _position);
                Buffer.BlockCopy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
