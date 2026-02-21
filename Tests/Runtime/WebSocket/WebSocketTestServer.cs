using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    internal sealed class WebSocketTestServerOptions
    {
        public int RejectHandshakeStatusCode { get; set; }

        public string RejectHandshakeReason { get; set; } = "Rejected";

        public int RejectHandshakeAfterConnectionCount { get; set; }

        public int EchoDelayMs { get; set; }

        public int CloseAfterMessageCount { get; set; }

        public int AbortAfterMessageCount { get; set; }

        public bool DisconnectFirstConnectionAfterHandshake { get; set; }

        public bool DisconnectEveryConnectionAfterHandshake { get; set; }

        public bool SendMaskedFrameOnFirstMessage { get; set; }

        public bool SendReservedOpcodeFrameOnFirstMessage { get; set; }

        public bool SendInvalidUtf8TextOnFirstMessage { get; set; }
    }

    /// <summary>
    /// In-process WebSocket server for deterministic runtime tests.
    /// </summary>
    internal sealed class WebSocketTestServer : IDisposable
    {
        private readonly WebSocketTestServerOptions _options;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly List<Task> _clientTasks = new List<Task>();
        private readonly object _clientTasksGate = new object();

        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private int _connectionCount;
        private int _messageCount;

        public WebSocketTestServer(WebSocketTestServerOptions options = null)
        {
            _options = options ?? new WebSocketTestServerOptions();

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public int MessageCount => Volatile.Read(ref _messageCount);

        public Uri CreateUri(string path = "/")
        {
            if (string.IsNullOrWhiteSpace(path))
                path = "/";

            if (path[0] != '/')
                path = "/" + path;

            return new Uri("ws://127.0.0.1:" + Port + path, UriKind.Absolute);
        }

        public void Dispose()
        {
            _shutdown.Cancel();

            try
            {
                _listener.Stop();
            }
            catch
            {
                // Best-effort listener shutdown.
            }

            try
            {
                _acceptLoop.GetAwaiter().GetResult();
            }
            catch
            {
                // Accept loop can fail during cancellation/listener stop.
            }

            Task[] snapshot;
            lock (_clientTasksGate)
            {
                snapshot = _clientTasks.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort per-client shutdown.
                }
            }

            _shutdown.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_shutdown.IsCancellationRequested)
                        break;
                    continue;
                }

                int connectionIndex = Interlocked.Increment(ref _connectionCount);

                var clientTask = Task.Run(
                    () => HandleClientAsync(client, connectionIndex, _shutdown.Token),
                    _shutdown.Token);

                lock (_clientTasksGate)
                {
                    _clientTasks.Add(clientTask);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, int connectionIndex, CancellationToken ct)
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            {
                string requestHead;
                try
                {
                    requestHead = await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                bool shouldRejectHandshake = _options.RejectHandshakeStatusCode > 0 &&
                    (_options.RejectHandshakeAfterConnectionCount <= 0 ||
                     connectionIndex > _options.RejectHandshakeAfterConnectionCount);

                if (shouldRejectHandshake)
                {
                    await WriteHandshakeRejectAsync(
                        stream,
                        _options.RejectHandshakeStatusCode,
                        _options.RejectHandshakeReason,
                        ct).ConfigureAwait(false);
                    return;
                }

                string clientKey = TryGetHeaderValue(requestHead, "Sec-WebSocket-Key");
                if (string.IsNullOrWhiteSpace(clientKey))
                {
                    await WriteHandshakeRejectAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
                    return;
                }

                await WriteHandshakeSuccessAsync(stream, clientKey.Trim(), ct).ConfigureAwait(false);

                if (_options.DisconnectEveryConnectionAfterHandshake ||
                    (_options.DisconnectFirstConnectionAfterHandshake && connectionIndex == 1))
                {
                    tcpClient.Close();
                    return;
                }

                var frameReader = new WebSocketFrameReader(rejectMaskedServerFrames: false);
                var assembler = new MessageAssembler();

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frameLease = await frameReader.ReadAsync(
                            stream,
                            assembler.FragmentedMessageInProgress,
                            ct).ConfigureAwait(false);

                        if (frameLease == null)
                            break;

                        if (!assembler.TryAssemble(frameLease, out var assembledMessage))
                            continue;

                        if (assembledMessage == null)
                            continue;

                        using (assembledMessage)
                        {
                            if (assembledMessage.IsControl)
                            {
                                if (assembledMessage.Opcode == WebSocketOpcode.Ping)
                                {
                                    await WriteServerFrameAsync(
                                        stream,
                                        WebSocketOpcode.Pong,
                                        assembledMessage.Payload,
                                        masked: false,
                                        ct).ConfigureAwait(false);
                                }
                                else if (assembledMessage.Opcode == WebSocketOpcode.Close)
                                {
                                    await WriteServerFrameAsync(
                                        stream,
                                        WebSocketOpcode.Close,
                                        assembledMessage.Payload,
                                        masked: false,
                                        ct).ConfigureAwait(false);
                                    break;
                                }

                                continue;
                            }

                            int messageIndex = Interlocked.Increment(ref _messageCount);

                            if (_options.SendMaskedFrameOnFirstMessage && messageIndex == 1)
                            {
                                await WriteServerFrameAsync(
                                    stream,
                                    WebSocketOpcode.Text,
                                    Encoding.UTF8.GetBytes("masked-server-frame"),
                                    masked: true,
                                    ct).ConfigureAwait(false);
                                continue;
                            }

                            if (_options.SendReservedOpcodeFrameOnFirstMessage && messageIndex == 1)
                            {
                                await WriteRawOpcodeFrameAsync(
                                    stream,
                                    rawOpcode: 0x03,
                                    Encoding.UTF8.GetBytes("reserved-opcode"),
                                    ct).ConfigureAwait(false);
                                continue;
                            }

                            if (_options.SendInvalidUtf8TextOnFirstMessage && messageIndex == 1)
                            {
                                await WriteServerFrameAsync(
                                    stream,
                                    WebSocketOpcode.Text,
                                    new byte[] { 0xC3, 0x28 },
                                    masked: false,
                                    ct).ConfigureAwait(false);
                                continue;
                            }

                            if (_options.EchoDelayMs > 0)
                            {
                                await Task.Delay(_options.EchoDelayMs, ct).ConfigureAwait(false);
                            }

                            await WriteServerFrameAsync(
                                stream,
                                assembledMessage.Opcode,
                                assembledMessage.Payload,
                                masked: false,
                                ct).ConfigureAwait(false);

                            if (_options.CloseAfterMessageCount > 0 &&
                                messageIndex >= _options.CloseAfterMessageCount)
                            {
                                await WriteCloseFrameAsync(
                                    stream,
                                    WebSocketCloseCode.NormalClosure,
                                    "server-close",
                                    ct).ConfigureAwait(false);
                                break;
                            }

                            if (_options.AbortAfterMessageCount > 0 &&
                                messageIndex >= _options.AbortAfterMessageCount)
                            {
                                tcpClient.Close();
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Expected during server disposal.
                }
                catch (IOException)
                {
                    // Client may disconnect abruptly in tests.
                }
                catch (ObjectDisposedException)
                {
                    // Stream/listener disposed during shutdown.
                }
                finally
                {
                    assembler.Reset();
                }
            }
        }

        private static async Task<string> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            var builder = new StringBuilder(1024);

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Unexpected EOF while reading request headers.");

                    builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    string text = builder.ToString();
                    int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end >= 0)
                        return text.Substring(0, end + 4);

                    if (builder.Length > 16 * 1024)
                        throw new IOException("Header block too large.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static string TryGetHeaderValue(string head, string headerName)
        {
            if (string.IsNullOrEmpty(head))
                return null;

            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string prefix = headerName + ":";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            return null;
        }

        private static async Task WriteHandshakeSuccessAsync(
            Stream stream,
            string clientKey,
            CancellationToken ct)
        {
            string accept = WebSocketConstants.ComputeAcceptKey(clientKey);
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n" +
                "\r\n";

            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteHandshakeRejectAsync(
            Stream stream,
            int statusCode,
            string reason,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Rejected";

            string body = "handshake rejected";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            string response =
                "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            byte[] headBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(headBytes, 0, headBytes.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteCloseFrameAsync(
            Stream stream,
            WebSocketCloseCode code,
            string reason,
            CancellationToken ct)
        {
            reason = reason ?? string.Empty;
            byte[] reasonPayload = TruncateUtf8(reason, WebSocketConstants.MaxCloseReasonUtf8Bytes);
            int reasonBytes = reasonPayload.Length;

            int payloadLength = 2 + reasonBytes;
            byte[] payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(payload, 0, 2), (ushort)code);
                if (reasonBytes > 0)
                {
                    Buffer.BlockCopy(reasonPayload, 0, payload, 2, reasonPayload.Length);
                }

                await WriteServerFrameAsync(
                    stream,
                    WebSocketOpcode.Close,
                    new ReadOnlyMemory<byte>(payload, 0, payloadLength),
                    masked: false,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        private static byte[] TruncateUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0)
                return Array.Empty<byte>();

            var utf8 = Encoding.UTF8;
            byte[] all = utf8.GetBytes(value);
            if (all.Length <= maxBytes)
                return all;

            int charCount = value.Length;
            while (charCount > 0)
            {
                charCount--;
                if (char.IsLowSurrogate(value[charCount]))
                {
                    charCount--;
                    if (charCount < 0)
                        break;
                }

                byte[] candidate = utf8.GetBytes(value.Substring(0, charCount + 1));
                if (candidate.Length <= maxBytes)
                    return candidate;
            }

            return Array.Empty<byte>();
        }

        private static async Task WriteRawOpcodeFrameAsync(
            Stream stream,
            byte rawOpcode,
            byte[] payload,
            CancellationToken ct)
        {
            if (payload == null)
                payload = Array.Empty<byte>();

            byte[] header = new byte[2];
            header[0] = (byte)(0x80 | (rawOpcode & 0x0F));
            header[1] = (byte)(payload.Length & 0x7F);

            await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteServerFrameAsync(
            Stream stream,
            WebSocketOpcode opcode,
            ReadOnlyMemory<byte> payload,
            bool masked,
            CancellationToken ct)
        {
            int payloadLength = payload.Length;
            byte[] header = new byte[14];
            int headerLength = 0;

            header[headerLength++] = (byte)(0x80 | (byte)opcode);
            if (payloadLength <= 125)
            {
                header[headerLength++] = (byte)((masked ? 0x80 : 0x00) | payloadLength);
            }
            else if (payloadLength <= ushort.MaxValue)
            {
                header[headerLength++] = (byte)((masked ? 0x80 : 0x00) | 126);
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(header, headerLength, 2), (ushort)payloadLength);
                headerLength += 2;
            }
            else
            {
                header[headerLength++] = (byte)((masked ? 0x80 : 0x00) | 127);
                BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(header, headerLength, 8), (ulong)payloadLength);
                headerLength += 8;
            }

            uint maskKey = 0x11223344u;
            byte[] maskedPayload = null;
            if (masked)
            {
                BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(header, headerLength, 4), maskKey);
                headerLength += 4;

                if (payloadLength > 0)
                {
                    maskedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);
                    payload.CopyTo(maskedPayload);
                    ApplyMask(maskedPayload, payloadLength, maskKey);
                }
            }

            try
            {
                await stream.WriteAsync(header, 0, headerLength, ct).ConfigureAwait(false);

                if (payloadLength > 0)
                {
                    if (masked && maskedPayload != null)
                    {
                        await stream.WriteAsync(maskedPayload, 0, payloadLength, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var bytes = payload.ToArray();
                        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                    }
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (maskedPayload != null)
                    ArrayPool<byte>.Shared.Return(maskedPayload);
            }
        }

        private static void ApplyMask(byte[] payload, int payloadLength, uint maskKey)
        {
            byte key0 = (byte)(maskKey >> 24);
            byte key1 = (byte)(maskKey >> 16);
            byte key2 = (byte)(maskKey >> 8);
            byte key3 = (byte)maskKey;

            for (int i = 0; i < payloadLength; i += 4)
            {
                payload[i] ^= key0;
                if (i + 1 < payloadLength) payload[i + 1] ^= key1;
                if (i + 2 < payloadLength) payload[i + 2] ^= key2;
                if (i + 3 < payloadLength) payload[i + 3] ^= key3;
            }
        }
    }

    internal sealed class TestTcpWebSocketTransport : IWebSocketTransport
    {
        public void Dispose()
        {
        }

        public async Task<Stream> ConnectAsync(
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken ct)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            int port = uri.IsDefaultPort ? 80 : uri.Port;
            var tcpClient = new TcpClient();

            using var registration = ct.Register(
                static state =>
                {
                    try { ((TcpClient)state).Close(); } catch { }
                },
                tcpClient);

            var connectTask = tcpClient.ConnectAsync(uri.Host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, connectTask))
                throw new OperationCanceledException(ct);

            await connectTask.ConfigureAwait(false);
            return new NetworkStream(tcpClient.Client, ownsSocket: true);
        }
    }
}
