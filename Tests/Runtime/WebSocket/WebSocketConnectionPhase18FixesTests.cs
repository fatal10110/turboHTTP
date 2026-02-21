using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketConnectionPhase18FixesTests
    {
        [Test]
        public void Abort_BeforeConnect_TransitionsToClosed()
        {
            var connection = new WebSocketConnection();
            try
            {
                connection.Abort();

                Assert.AreEqual(WebSocketState.Closed, connection.State);
                Assert.IsTrue(connection.CloseStatus.HasValue);
                Assert.AreEqual(WebSocketCloseCode.AbnormalClosure, connection.CloseStatus.Value.Code);
            }
            finally
            {
                connection.Dispose();
            }
        }

        [Test]
        public void CloseStatus_LongReason_TruncatesToUtf8Limit()
        {
            var longReason = new string('a', 200);

            var status = new WebSocketCloseStatus(WebSocketCloseCode.NormalClosure, longReason);

            int reasonBytes = Encoding.UTF8.GetByteCount(status.Reason);
            Assert.AreEqual(WebSocketConstants.MaxCloseReasonUtf8Bytes, reasonBytes);
            Assert.AreEqual(longReason.Substring(0, WebSocketConstants.MaxCloseReasonUtf8Bytes), status.Reason);
        }

        [Test]
        public void PrefetchedStream_DisposeAsync_UsesInnerDisposeAsync()
        {
            AssertAsync.Run(async () =>
            {
                var inner = new TrackingAsyncDisposeStream();
                var prefetchedStreamType = typeof(WebSocketConnection).GetNestedType("PrefetchedStream", BindingFlags.NonPublic);
                Assert.IsNotNull(prefetchedStreamType, "Failed to resolve WebSocketConnection.PrefetchedStream.");

                var wrapped = (Stream)Activator.CreateInstance(
                    prefetchedStreamType,
                    new object[] { inner, new byte[] { 1, 2, 3 } });

                await wrapped.DisposeAsync().ConfigureAwait(false);

                Assert.IsTrue(inner.DisposeAsyncCalled);
            });
        }

        [Test]
        public void KeepAlive_ReceivesPongWithinTightTimeout_ConnectionStaysOpen()
        {
            AssertAsync.Run(async () =>
            {
                var options = new WebSocketConnectionOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(2),
                    CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                    PingInterval = TimeSpan.FromMilliseconds(25),
                    PongTimeout = TimeSpan.FromMilliseconds(45),
                    IdleTimeout = TimeSpan.Zero
                };

                var connection = new WebSocketConnection();
                using var transport = new LoopbackWebSocketTransport(TimeSpan.FromMilliseconds(10));

                try
                {
                    await connection.ConnectAsync(
                        new Uri("ws://localhost/phase18-fixes"),
                        transport,
                        options,
                        CancellationToken.None).ConfigureAwait(false);

                    await Task.Delay(TimeSpan.FromMilliseconds(180)).ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Open, connection.State);
                    Assert.IsNull(connection.TerminalError);

                    await connection.CloseAsync(
                        WebSocketCloseCode.NormalClosure,
                        "done",
                        CancellationToken.None).ConfigureAwait(false);

                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        private sealed class TrackingAsyncDisposeStream : MemoryStream
        {
            public bool DisposeAsyncCalled { get; private set; }

            public override ValueTask DisposeAsync()
            {
                DisposeAsyncCalled = true;
                return base.DisposeAsync();
            }
        }

        private sealed class LoopbackWebSocketTransport : IWebSocketTransport
        {
            private readonly TimeSpan _pongDelay;
            private readonly CancellationTokenSource _serverCts = new CancellationTokenSource();

            private Stream _clientStream;
            private Stream _serverStream;
            private Task _serverTask;
            private int _connected;
            private int _disposed;

            public LoopbackWebSocketTransport(TimeSpan pongDelay)
            {
                _pongDelay = pongDelay;
            }

            public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(LoopbackWebSocketTransport));

                if (Interlocked.CompareExchange(ref _connected, 1, 0) != 0)
                    throw new InvalidOperationException("Loopback transport supports a single connection.");

                var duplex = new TestDuplexStream();
                _clientStream = duplex.ClientStream;
                _serverStream = duplex.ServerStream;
                _serverTask = RunServerAsync(_serverStream, _pongDelay, _serverCts.Token);

                return Task.FromResult(_clientStream);
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    return;

                _serverCts.Cancel();
                SafeDispose(_clientStream);
                SafeDispose(_serverStream);

                if (_serverTask != null)
                {
                    try
                    {
                        _serverTask.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Best-effort test transport shutdown.
                    }
                }

                _serverCts.Dispose();
            }

            private static async Task RunServerAsync(Stream stream, TimeSpan pongDelay, CancellationToken ct)
            {
                string requestHeaders = await ReadHandshakeRequestAsync(stream, ct).ConfigureAwait(false);
                string clientKey = ExtractHeaderValue(requestHeaders, "Sec-WebSocket-Key");
                if (string.IsNullOrWhiteSpace(clientKey))
                    throw new InvalidOperationException("Handshake request did not include Sec-WebSocket-Key.");

                string accept = WebSocketConstants.ComputeAcceptKey(clientKey.Trim());
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";

                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                var reader = new WebSocketFrameReader(rejectMaskedServerFrames: false);

                while (!ct.IsCancellationRequested)
                {
                    var lease = await reader.ReadAsync(stream, fragmentedMessageInProgress: false, ct)
                        .ConfigureAwait(false);

                    if (lease == null)
                        break;

                    using (lease)
                    {
                        var frame = lease.Frame;
                        if (frame.Opcode == WebSocketOpcode.Ping)
                        {
                            if (pongDelay > TimeSpan.Zero)
                                await Task.Delay(pongDelay, ct).ConfigureAwait(false);

                            await WriteServerFrameAsync(stream, WebSocketOpcode.Pong, frame.Payload, ct)
                                .ConfigureAwait(false);
                            continue;
                        }

                        if (frame.Opcode == WebSocketOpcode.Close)
                        {
                            await WriteServerFrameAsync(stream, WebSocketOpcode.Close, frame.Payload, ct)
                                .ConfigureAwait(false);
                            break;
                        }
                    }
                }
            }

            private static async Task<string> ReadHandshakeRequestAsync(Stream stream, CancellationToken ct)
            {
                var buffer = new byte[1024];
                var builder = new StringBuilder(1024);

                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Unexpected EOF while reading handshake request.");

                    builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    string text = builder.ToString();
                    int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                        return text.Substring(0, headerEnd + 4);
                }
            }

            private static string ExtractHeaderValue(string headersText, string headerName)
            {
                var lines = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string prefix = headerName + ":";

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return lines[i].Substring(prefix.Length).Trim();
                }

                return null;
            }

            private static async Task WriteServerFrameAsync(
                Stream stream,
                WebSocketOpcode opcode,
                ReadOnlyMemory<byte> payload,
                CancellationToken ct)
            {
                if (payload.Length > WebSocketConstants.MaxControlFramePayloadLength)
                    throw new ArgumentOutOfRangeException(nameof(payload), "Control payload exceeds 125 bytes.");

                byte[] header = new byte[2];
                header[0] = (byte)(0x80 | (byte)opcode);
                header[1] = (byte)payload.Length;

                await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    var payloadBytes = payload.ToArray();
                    await stream.WriteAsync(payloadBytes, 0, payloadBytes.Length, ct).ConfigureAwait(false);
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            private static void SafeDispose(IDisposable disposable)
            {
                if (disposable == null)
                    return;

                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best-effort shutdown in tests.
                }
            }
        }
    }
}
