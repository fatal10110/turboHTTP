using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #226: a proxy that never answers CONNECT, or an origin that never sends the 101, must not hold
    /// <c>ConnectAsync</c> past the handshake timeout or a caller cancellation. Mono/IL2CPP
    /// NetworkStream ignores the token once a read is pending, so the stream is disposed on
    /// cancellation; the outcome is cancellation or a handshake timeout, never a proxy/tunnel failure,
    /// and the socket is closed.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketConnectCancellationTests
    {
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMilliseconds(500);

        // Guard only: the connect must finish long before this, it never synchronizes the test.
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        [Test]
        public void ProxyConnect_SilentProxy_HandshakeTimeout_FailsAsTimeoutAndClosesSocket()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new SilentServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                var options = CreateOptions(HandshakeTimeout);
                options.ProxySettings = new WebSocketProxySettings(new Uri("http://127.0.0.1:" + proxy.Port));

                var ex = await CaptureConnectFailureAsync(
                    client.ConnectAsync(new Uri("ws://127.0.0.1:9/silent-proxy"), options, CancellationToken.None));

                AssertHandshakeTimeout(ex);
                await AssertCompletesAsync(proxy.ClosedByClient(0), "proxy socket was not closed on timeout");
            });
        }

        [Test]
        public void ProxyConnect_SilentProxy_CallerCancel_ThrowsOperationCanceledAndClosesSocket()
        {
            AssertAsync.Run(async () =>
            {
                using var proxy = new SilentServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());
                var options = CreateOptions(TimeSpan.FromSeconds(30));
                options.ProxySettings = new WebSocketProxySettings(new Uri("http://127.0.0.1:" + proxy.Port));
                using var cts = new CancellationTokenSource();

                var connect = client.ConnectAsync(new Uri("ws://127.0.0.1:9/silent-proxy"), options, cts.Token);
                await AssertCompletesAsync(proxy.Accepted(0), "client never reached the proxy");
                cts.Cancel();

                var ex = await CaptureConnectFailureAsync(connect);

                Assert.IsInstanceOf<OperationCanceledException>(ex, ex.ToString());
                await AssertCompletesAsync(proxy.ClosedByClient(0), "proxy socket was not closed on cancel");
            });
        }

        [Test]
        public void Upgrade_SilentOrigin_HandshakeTimeout_FailsAsTimeoutAndClosesSocket()
        {
            AssertAsync.Run(async () =>
            {
                using var origin = new SilentServer();
                await using var client = new WebSocketClient(new RawSocketWebSocketTransport());

                var ex = await CaptureConnectFailureAsync(client.ConnectAsync(
                    new Uri("ws://127.0.0.1:" + origin.Port + "/silent-origin"),
                    CreateOptions(HandshakeTimeout),
                    CancellationToken.None));

                AssertHandshakeTimeout(ex);
                await AssertCompletesAsync(origin.ClosedByClient(0), "origin socket was not closed on timeout");
            });
        }

        [Test]
        public void Upgrade_ReadIgnoresToken_HandshakeTimeout_DisposesStream()
        {
            AssertAsync.Run(async () =>
            {
                // The stream behaves like Mono/IL2CPP NetworkStream: a pending read ignores the token.
                using var transport = new TokenIgnoringTransport();
                await using var client = new WebSocketClient(transport);

                var ex = await CaptureConnectFailureAsync(client.ConnectAsync(
                    new Uri("ws://127.0.0.1/ignores-token"),
                    CreateOptions(HandshakeTimeout),
                    CancellationToken.None));

                AssertHandshakeTimeout(ex);
                Assert.IsTrue(transport.Stream.IsDisposed, "the handshake stream was not disposed");
            });
        }

        [Test]
        public void Upgrade_ReadIgnoresToken_CallerCancel_ThrowsOperationCanceledAndDisposesStream()
        {
            AssertAsync.Run(async () =>
            {
                using var transport = new TokenIgnoringTransport();
                await using var client = new WebSocketClient(transport);
                using var cts = new CancellationTokenSource();

                var connect = client.ConnectAsync(
                    new Uri("ws://127.0.0.1/ignores-token"),
                    CreateOptions(TimeSpan.FromSeconds(30)),
                    cts.Token);
                await AssertCompletesAsync(transport.ReadPending, "the upgrade response read never started");
                cts.Cancel();

                var ex = await CaptureConnectFailureAsync(connect);

                Assert.IsInstanceOf<OperationCanceledException>(ex, ex.ToString());
                Assert.IsTrue(transport.Stream.IsDisposed, "the handshake stream was not disposed");
            });
        }

        private static void AssertHandshakeTimeout(Exception ex)
        {
            var wsEx = ex as WebSocketException;
            Assert.IsNotNull(wsEx, "expected a WebSocketException, got " + ex);
            Assert.AreNotEqual(WebSocketError.ProxyTunnelFailed, wsEx.Error, wsEx.ToString());
            Assert.AreNotEqual(WebSocketError.ProxyConnectionFailed, wsEx.Error, wsEx.ToString());
            Assert.AreNotEqual(WebSocketError.HandshakeFailed, wsEx.Error, wsEx.ToString());
            Assert.AreEqual(WebSocketError.ConnectionClosed, wsEx.Error, wsEx.ToString());
            Assert.IsInstanceOf<OperationCanceledException>(wsEx.InnerException, wsEx.ToString());
            StringAssert.Contains("timed out", wsEx.Message);
        }

        private static async Task<Exception> CaptureConnectFailureAsync(Task connect)
        {
            var winner = await Task.WhenAny(connect, Task.Delay(Guard));
            Assert.AreSame(connect, winner, "ConnectAsync outlived its handshake timeout / cancellation");

            try
            {
                await connect;
            }
            catch (Exception ex)
            {
                return ex;
            }

            Assert.Fail("ConnectAsync succeeded against a silent peer");
            return null;
        }

        private static async Task AssertCompletesAsync(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard));
            Assert.AreSame(task, winner, message);
        }

        private static WebSocketConnectionOptions CreateOptions(TimeSpan handshakeTimeout)
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = handshakeTimeout,
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                PongTimeout = TimeSpan.FromMilliseconds(200),
                ReceiveQueueCapacity = 4,
                AllowCleartext = true
            };
        }

        /// <summary>Returns one <see cref="TokenIgnoringStream"/>; writes succeed, reads never answer.</summary>
        private sealed class TokenIgnoringTransport : IWebSocketTransport
        {
            public TokenIgnoringStream Stream { get; } = new TokenIgnoringStream();

            public Task ReadPending => Stream.ReadPending;

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                return Task.FromResult<Stream>(Stream);
            }

            public void Dispose()
            {
                Stream.Dispose();
            }
        }

        /// <summary>
        /// A read stays pending whatever its token says and faults only when the stream is disposed,
        /// as a pending Mono/IL2CPP NetworkStream read does.
        /// </summary>
        private sealed class TokenIgnoringStream : Stream
        {
            private readonly TaskCompletionSource<int> _read =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _readPending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public Task ReadPending => _readPending.Task;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _readPending.TrySetResult(true);
                return _read.Task;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(TokenIgnoringStream));
                return Task.CompletedTask;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(TokenIgnoringStream));
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _read.TrySetException(new ObjectDisposedException(nameof(TokenIgnoringStream)));
                base.Dispose(disposing);
            }
        }

        /// <summary>Accepts connections, reads whatever arrives, never writes (copied from #162's tests).</summary>
        private sealed class SilentServer : IDisposable
        {
            private const int MaxClients = 4;
            private readonly TcpListener _listener;
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly object _gate = new object();
            private readonly TaskCompletionSource<bool>[] _accepted = NewSignals();
            private readonly TaskCompletionSource<bool>[] _closed = NewSignals();

            public SilentServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port { get; }

            public Task Accepted(int index) => _accepted[index].Task;

            /// <summary>Completes when the client side closes connection <paramref name="index"/>.</summary>
            public Task ClosedByClient(int index) => _closed[index].Task;

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
                lock (_gate)
                {
                    foreach (var client in _clients)
                    {
                        try { client.Close(); } catch { }
                    }
                }
            }

            private static TaskCompletionSource<bool>[] NewSignals()
            {
                var signals = new TaskCompletionSource<bool>[MaxClients];
                for (var i = 0; i < signals.Length; i++)
                    signals[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return signals;
            }

            private async Task AcceptLoopAsync()
            {
                for (var index = 0; index < MaxClients; index++)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    lock (_gate)
                        _clients.Add(client);
                    _accepted[index].TrySetResult(true);
                    _ = ReadUntilClosedAsync(client, _closed[index]);
                }
            }

            private static async Task ReadUntilClosedAsync(TcpClient client, TaskCompletionSource<bool> closed)
            {
                var buffer = new byte[1024];
                try
                {
                    var stream = client.GetStream();
                    while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }
                catch (Exception)
                {
                    // Reset by the client also counts as closed.
                }

                closed.TrySetResult(true);
            }
        }
    }
}
