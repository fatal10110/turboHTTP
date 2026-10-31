using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// Single-connection in-memory WebSocket server for deterministic tests. It completes the handshake,
    /// answers pings, records every other client frame and acts as a strict RFC 6455 section 5.4 peer:
    /// a new data frame while a fragmented message is in progress (or a stray continuation) is recorded
    /// as a framing violation, which a real server would answer with close code 1002.
    /// </summary>
    internal sealed class InMemoryWebSocketPeer : IWebSocketTransport
    {
        private readonly Func<Stream, Stream> _wrapClientStream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<ReceivedFrame> _frames = new ConcurrentQueue<ReceivedFrame>();
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0);
        private readonly TaskCompletionSource<bool> _readerDone =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Stream _clientStream;
        private Stream _serverStream;
        private int _connected;
        private int _disposed;
        private int _violations;
        private int _serverCloseSent;

        public InMemoryWebSocketPeer(Func<Stream, Stream> wrapClientStream = null)
        {
            _wrapClientStream = wrapClientStream;
        }

        /// <summary>Number of RFC 6455 section 5.4 framing violations seen from the client.</summary>
        public int FramingViolations => Volatile.Read(ref _violations);

        /// <summary>Optional Sec-WebSocket-Extensions value sent in the handshake response.</summary>
        public string HandshakeExtensionsHeader { get; set; }

        /// <summary>Completes when the server stops reading (client EOF, close or error).</summary>
        public Task ReaderCompleted => _readerDone.Task;

        public Task<Stream> ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _connected, 1, 0) != 0)
                throw new InvalidOperationException("In-memory peer supports a single connection.");

            var duplex = new TestDuplexStream();
            _serverStream = duplex.ServerStream;
            _clientStream = _wrapClientStream != null
                ? _wrapClientStream(duplex.ClientStream)
                : duplex.ClientStream;

            _ = RunServerAsync(_serverStream, _cts.Token);
            return Task.FromResult(_clientStream);
        }

        public Task SendTextAsync(string text)
        {
            return SendFrameAsync(WebSocketOpcode.Text, Encoding.UTF8.GetBytes(text), fin: true);
        }

        /// <summary>Writes <paramref name="count"/> text messages "m0".."m{count-1}" in a single write.</summary>
        public async Task SendManyTextAsync(int count)
        {
            using var buffer = new MemoryStream();
            for (int i = 0; i < count; i++)
                AppendFrame(buffer, WebSocketOpcode.Text, Encoding.UTF8.GetBytes("m" + i), fin: true);

            await WriteRawAsync(buffer.ToArray()).ConfigureAwait(false);
        }

        public async Task SendFrameAsync(WebSocketOpcode opcode, byte[] payload, bool fin)
        {
            using var buffer = new MemoryStream();
            AppendFrame(buffer, opcode, payload, fin);
            if (opcode == WebSocketOpcode.Close)
                Interlocked.Exchange(ref _serverCloseSent, 1);

            await WriteRawAsync(buffer.ToArray()).ConfigureAwait(false);
        }

        /// <summary>Writes raw bytes to the client (hand-built frames, e.g. RSV bits or header-only frames).</summary>
        public Task SendRawAsync(byte[] bytes)
        {
            return WriteRawAsync(bytes);
        }

        /// <summary>Returns the next recorded client frame (pings are answered, not recorded).</summary>
        public async Task<ReceivedFrame> ReadFrameAsync(TimeSpan timeout)
        {
            if (!await _frameSignal.WaitAsync(timeout).ConfigureAwait(false))
                throw new TimeoutException("No client frame arrived within " + timeout + ".");

            _frames.TryDequeue(out var frame);
            return frame;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _cts.Cancel();
            SafeDispose(_clientStream);
            SafeDispose(_serverStream);
        }

        private async Task RunServerAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                await CompleteHandshakeAsync(stream, ct).ConfigureAwait(false);

                var reader = new WebSocketFrameReader(rejectMaskedServerFrames: false);
                bool fragmentInProgress = false;

                while (!ct.IsCancellationRequested)
                {
                    WebSocketFrameReadLease lease;
                    try
                    {
                        lease = await reader.ReadAsync(stream, fragmentInProgress, ct).ConfigureAwait(false);
                    }
                    catch (WebSocketProtocolException)
                    {
                        Interlocked.Increment(ref _violations);
                        break;
                    }

                    if (lease == null)
                        break;

                    using (lease)
                    {
                        var frame = lease.Frame;
                        if (frame.Opcode == WebSocketOpcode.Ping)
                        {
                            await SendFrameAsync(WebSocketOpcode.Pong, frame.Payload.ToArray(), fin: true)
                                .ConfigureAwait(false);
                            continue;
                        }

                        if (frame.IsDataFrame)
                        {
                            bool isContinuation = frame.Opcode == WebSocketOpcode.Continuation;
                            if (isContinuation != fragmentInProgress)
                                Interlocked.Increment(ref _violations);

                            fragmentInProgress = !frame.IsFinal;
                        }

                        _frames.Enqueue(new ReceivedFrame(frame.Opcode, frame.IsFinal, frame.Payload.ToArray()));
                        _frameSignal.Release();

                        if (frame.Opcode == WebSocketOpcode.Close)
                        {
                            if (Interlocked.CompareExchange(ref _serverCloseSent, 1, 0) == 0)
                            {
                                await SendFrameAsync(WebSocketOpcode.Close, frame.Payload.ToArray(), fin: true)
                                    .ConfigureAwait(false);
                            }

                            break;
                        }
                    }
                }
            }
            catch
            {
                // Client EOF mid-frame, disposal or cancellation end the server loop.
            }
            finally
            {
                _readerDone.TrySetResult(true);
            }
        }

        private async Task CompleteHandshakeAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[1024];
            var builder = new StringBuilder(1024);
            string request;

            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Unexpected EOF while reading handshake request.");

                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                string text = builder.ToString();
                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    request = text.Substring(0, headerEnd + 4);
                    break;
                }
            }

            string clientKey = null;
            foreach (var line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    clientKey = line.Substring("Sec-WebSocket-Key:".Length).Trim();
            }

            if (string.IsNullOrEmpty(clientKey))
                throw new InvalidOperationException("Handshake request did not include Sec-WebSocket-Key.");

            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + WebSocketConstants.ComputeAcceptKey(clientKey) + "\r\n" +
                (string.IsNullOrEmpty(HandshakeExtensionsHeader)
                    ? string.Empty
                    : "Sec-WebSocket-Extensions: " + HandshakeExtensionsHeader + "\r\n") +
                "\r\n";

            await WriteRawAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
        }

        private async Task WriteRawAsync(byte[] bytes)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _serverStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await _serverStream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static void AppendFrame(MemoryStream buffer, WebSocketOpcode opcode, byte[] payload, bool fin)
        {
            int length = payload?.Length ?? 0;
            buffer.WriteByte((byte)((fin ? 0x80 : 0x00) | (byte)opcode));

            if (length < 126)
            {
                buffer.WriteByte((byte)length);
            }
            else if (length <= ushort.MaxValue)
            {
                buffer.WriteByte(126);
                buffer.WriteByte((byte)(length >> 8));
                buffer.WriteByte((byte)length);
            }
            else
            {
                buffer.WriteByte(127);
                for (int shift = 56; shift >= 0; shift -= 8)
                    buffer.WriteByte((byte)((long)length >> shift));
            }

            if (length > 0)
                buffer.Write(payload, 0, length);
        }

        private static void SafeDispose(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Best-effort test shutdown.
            }
        }

        internal readonly struct ReceivedFrame
        {
            public ReceivedFrame(WebSocketOpcode opcode, bool isFinal, byte[] payload)
            {
                Opcode = opcode;
                IsFinal = isFinal;
                Payload = payload;
            }

            public WebSocketOpcode Opcode { get; }

            public bool IsFinal { get; }

            public byte[] Payload { get; }
        }
    }

    /// <summary>
    /// Client-side stream wrapper that honours cancellation on every write and flush (like a socket) and
    /// cancels a token source after a given number of writes or flushes once armed.
    /// </summary>
    internal sealed class CancelAfterWriteStream : Stream
    {
        private readonly Stream _inner;
        private CancellationTokenSource _cancelSource;
        private int _cancelAfterWrites;
        private int _cancelAfterFlushes;
        private int _writes;
        private int _flushes;

        public CancelAfterWriteStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Cancels <paramref name="source"/> after the Nth write (or Nth flush) from now.</summary>
        public void Arm(CancellationTokenSource source, int afterWrites = 0, int afterFlushes = 0)
        {
            Volatile.Write(ref _writes, 0);
            Volatile.Write(ref _flushes, 0);
            _cancelAfterWrites = afterWrites;
            _cancelAfterFlushes = afterFlushes;
            Volatile.Write(ref _cancelSource, source);
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
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);

            var source = Volatile.Read(ref _cancelSource);
            if (source != null && _cancelAfterWrites > 0 && Interlocked.Increment(ref _writes) == _cancelAfterWrites)
                source.Cancel();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask(WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken));
        }

        public override void Flush() => _inner.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);

            var source = Volatile.Read(ref _cancelSource);
            if (source != null && _cancelAfterFlushes > 0 && Interlocked.Increment(ref _flushes) == _cancelAfterFlushes)
                source.Cancel();
        }

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
