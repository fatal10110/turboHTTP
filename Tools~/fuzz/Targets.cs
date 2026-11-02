using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.JSON.Lite;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tools.Fuzz
{
    /// <summary>
    /// An input the oracle rejects: an exception type the parser must never throw, a hang, or a
    /// broken invariant. Every target lets this (and any exception it does not expect) escape, so
    /// libFuzzer and the mutation loop record the input as a crash.
    /// </summary>
    internal sealed class FuzzFindingException : Exception
    {
        public FuzzFindingException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The fuzz targets. Each one feeds a peer-controlled byte sequence to one parser and accepts only
    /// the typed failures that parser documents; anything else escapes as a finding.
    /// </summary>
    internal static class Targets
    {
        /// <summary>One async operation inside a target is bounded by this; a longer one is a hang.</summary>
        internal const int HangMilliseconds = 10_000;

        // Bounds for the WebSocket targets: small enough that a hostile length cannot exhaust memory in
        // the fuzzing process, large enough for every seed.
        private const int WsMaxFrameSize = 64 * 1024;
        private const int WsMaxMessageSize = 256 * 1024;
        private const int WsMaxFragments = 64;

        /// <summary>Set by <c>replay --trace</c>: print each expected failure (seed corpus QA).</summary>
        internal static bool Trace;

        private static void Expected(Exception ex)
        {
            if (Trace)
                Console.WriteLine($"  expected {RootCause(ex).GetType().Name}: {RootCause(ex).Message}");
        }

        internal static readonly IReadOnlyDictionary<string, Action<byte[]>> All =
            new SortedDictionary<string, Action<byte[]>>(StringComparer.Ordinal)
            {
                ["h1-response"] = data => Sync.Run(() => H1ResponseAsync(data)),
                ["h1-chunked-body"] = data => Sync.Run(() => H1ChunkedBodyAsync(data)),
                ["hpack"] = Hpack,
                ["h2-frames"] = data => Sync.Run(() => H2FramesAsync(data)),
                ["h2-connection"] = data => Sync.Run(() => H2ConnectionAsync(data)),
                ["ws-frames"] = data => Sync.Run(() => WsFramesAsync(data)),
                ["ws-inflate"] = WsInflate,
                ["json"] = Json,
            };

        // --- HTTP/1.1 (RFC 9112) ---

        /// <summary>
        /// The whole buffered response path: status line, header fields (obs-fold, bare LF), 1xx
        /// interim responses, framing (Content-Length / Transfer-Encoding) and the buffered body with
        /// chunked trailers. Input: the raw response bytes for a GET.
        /// </summary>
        private static async Task H1ResponseAsync(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            try
            {
                var parsed = await Http11ResponseParser.ParseAsync(stream, HttpMethod.GET, CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    if (parsed.Headers == null)
                        throw new FuzzFindingException("parsed response without headers");
                    if (parsed.SegmentedBody == null && parsed.Body.Length > data.Length)
                        throw new FuzzFindingException($"body of {parsed.Body.Length} bytes from a {data.Length}-byte input");
                }
                finally
                {
                    parsed.ReleaseBodyBuffers();
                }
            }
            catch (Exception ex) when (IsH1Failure(ex))
            {
                Expected(ex);
            }
        }

        /// <summary>
        /// The streaming chunked decoder (Http11ResponseBodySource): chunk-size lines, extensions,
        /// CRLF terminators and the trailer section, read through a caller buffer whose size the first
        /// input byte picks (1..64) so chunk boundaries fall at every offset.
        /// Input: [read size][chunked body bytes].
        /// </summary>
        private static async Task H1ChunkedBodyAsync(byte[] data)
        {
            if (data.Length == 0)
                return;

            int readSize = (data[0] % 64) + 1;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var stream = new MemoryStream(data, 1, data.Length - 1, writable: false);
            using var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = Http11ResponseBodyKind.Chunked
            };
            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "fuzz.test", 80, false));

            await using var source = new Http11ResponseBodySource(
                head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));
            try
            {
                var buffer = new byte[readSize];
                long total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (read < 0 || read > readSize)
                        throw new FuzzFindingException($"ReadAsync returned {read} for a {readSize}-byte buffer");
                    total += read;
                    if (total > data.Length)
                        throw new FuzzFindingException($"decoded {total} body bytes from a {data.Length}-byte input");
                }

                var trailers = await source.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                if (trailers == null)
                    throw new FuzzFindingException("GetTrailersAsync returned null after a complete body");
            }
            catch (Exception ex) when (IsH1Failure(ex))
            {
                Expected(ex);
            }
        }

        /// <summary>
        /// The failures the HTTP/1.1 parser and body source document: IOException (truncated input,
        /// limits), FormatException (malformed syntax), NotSupportedException (a transfer coding other
        /// than chunked), raw or wrapped in UHttpException the way RawSocketTransport maps them.
        /// </summary>
        private static bool IsH1Failure(Exception ex)
        {
            if (ex is FuzzFindingException)
                return false;
            var root = RootCause(ex);
            return root is IOException || root is FormatException || root is NotSupportedException ||
                (root is UHttpException && root.InnerException == null);
        }

        // --- HPACK (RFC 7541) ---

        /// <summary>
        /// A sequence of header blocks decoded on one decoder, so the dynamic table (insertion,
        /// eviction, size updates) carries state between blocks. Input: repeated [u16 BE length][block];
        /// a short tail is one more block. The first COMPRESSION_ERROR ends the connection.
        /// </summary>
        private static void Hpack(byte[] data)
        {
            var decoder = new HpackDecoder();
            var headers = new List<(string Name, string Value)>();
            int pos = 0;
            try
            {
                while (pos < data.Length)
                {
                    int length;
                    if (data.Length - pos < 2)
                    {
                        length = data.Length - pos;
                    }
                    else
                    {
                        length = Math.Min((data[pos] << 8) | data[pos + 1], data.Length - pos - 2);
                        pos += 2;
                    }

                    decoder.Decode(data, pos, length, headers);
                    foreach (var (name, value) in headers)
                    {
                        if (name == null || value == null)
                            throw new FuzzFindingException("decoded a null header name or value");
                    }

                    pos += length;
                }
            }
            catch (HpackDecodingException ex)
            {
                Expected(ex);
            }
        }

        // --- HTTP/2 frames (RFC 9113) ---

        /// <summary>
        /// The frame codec (9-octet header, 24-bit length, reserved bit, SETTINGS_MAX_FRAME_SIZE) and
        /// the SETTINGS payload parser with value validation. Input: a server frame stream.
        /// </summary>
        private static async Task H2FramesAsync(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            var codec = new Http2FrameCodec(stream);
            var settings = new Http2Settings();
            try
            {
                while (true)
                {
                    using var lease = await codec.ReadFrameLeaseAsync(Http2Constants.DefaultMaxFrameSize, CancellationToken.None)
                        .ConfigureAwait(false);
                    var frame = lease.Frame;
                    if (frame.Length < 0 || frame.Length > Http2Constants.DefaultMaxFrameSize)
                        throw new FuzzFindingException($"frame length {frame.Length} passed the size check");
                    if (frame.StreamId < 0)
                        throw new FuzzFindingException("reserved stream-id bit leaked into StreamId");

                    if (frame.Type == Http2FrameType.Settings && !frame.HasFlag(Http2FrameFlags.Ack))
                    {
                        foreach (var (id, value) in Http2Settings.ParsePayload(frame.Payload, frame.Length))
                            settings.Apply(id, value);
                    }
                }
            }
            catch (IOException ex)
            {
                Expected(ex);
            }
            catch (Http2ProtocolException ex)
            {
                Expected(ex);
            }
        }

        /// <summary>
        /// The whole HTTP/2 read loop: an Http2Connection over an in-memory stream, a GET on stream 1,
        /// then the input as the server's frames (HEADERS/CONTINUATION padding and priority, DATA
        /// padding and flow control, SETTINGS, PING, GOAWAY, RST_STREAM, WINDOW_UPDATE, PUSH_PROMISE).
        /// The input ends in EOF, so every dispatch must finish; one that does not is a hang. A failure
        /// whose root cause is not a protocol, compression or I/O error is a finding.
        /// </summary>
        private static async Task H2ConnectionAsync(byte[] data)
        {
            var stream = new H2FuzzStream(data);
            var options = new Http2Options { PingInterval = TimeSpan.Zero };
            var connection = new Http2Connection(stream, "fuzz.test", 443, options);
            var handler = new FuzzHttpHandler();
            try
            {
                await Bounded(connection.InitializeAsync(CancellationToken.None), "InitializeAsync").ConfigureAwait(false);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://fuzz.test/"));
                var dispatch = connection.DispatchAsync(request, handler, new RequestContext(request), CancellationToken.None);
                try
                {
                    await Bounded(dispatch, "DispatchAsync").ConfigureAwait(false);
                }
                catch (FuzzFindingException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ExpectH2Failure(ex, "DispatchAsync");
                }

                if (handler.BodyTask != null)
                {
                    try
                    {
                        await Bounded(handler.BodyTask, "response body").ConfigureAwait(false);
                    }
                    catch (FuzzFindingException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ExpectH2Failure(ex, "response body");
                    }
                }

                if (handler.Error != null)
                    ExpectH2Failure(handler.Error, "OnResponseError");
            }
            finally
            {
                connection.Dispose();
                stream.OpenGate();
            }
        }

        private static void ExpectH2Failure(Exception ex, string where)
        {
            var root = RootCause(ex);
            if (root is Http2ProtocolException || root is HpackDecodingException || root is IOException ||
                root is OperationCanceledException || root is UHttpException)
            {
                Expected(ex);
                return;
            }

            throw new FuzzFindingException($"{where} failed with {root.GetType().FullName}: {root.Message}", ex);
        }

        /// <summary>Unwraps UHttpException.HttpError.InnerException and single AggregateExceptions.</summary>
        private static Exception RootCause(Exception ex)
        {
            while (true)
            {
                if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                    ex = aggregate.InnerExceptions[0];
                else if (ex is UHttpException http && http.HttpError?.InnerException != null)
                    ex = http.HttpError.InnerException;
                else
                    return ex;
            }
        }

        private static async Task Bounded(Task task, string what)
        {
            using (var timer = new CancellationTokenSource())
            {
                if (await Task.WhenAny(task, Task.Delay(HangMilliseconds, timer.Token)).ConfigureAwait(false) != task)
                    throw new FuzzFindingException($"hang: {what} did not complete within {HangMilliseconds} ms after EOF");
                timer.Cancel();
            }

            await task.ConfigureAwait(false);
        }

        // --- WebSocket (RFC 6455, RFC 7692) ---

        /// <summary>
        /// Server frames through the frame reader (opcodes, RSV bits, masking, 7/16/64-bit lengths,
        /// control-frame rules), the message assembler (fragmentation, interleaved control frames,
        /// UTF-8) and permessage-deflate for RSV1 messages. Input: a server frame stream.
        /// </summary>
        private static async Task WsFramesAsync(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            var reader = new WebSocketFrameReader(WsMaxFrameSize, allowedRsvMask: 0x40,
                rejectMaskedServerFrames: true, maxMessageSize: WsMaxMessageSize);
            var assembler = new MessageAssembler(WsMaxMessageSize, WsMaxFragments);
            using var deflate = NegotiatedDeflate();
            bool fragmented = false;
            try
            {
                while (true)
                {
                    var lease = await reader.ReadAsync(stream, fragmented, CancellationToken.None).ConfigureAwait(false);
                    if (lease == null)
                        return;

                    var frame = lease.Frame;
                    if (frame.PayloadLength > WsMaxFrameSize)
                        throw new FuzzFindingException($"frame of {frame.PayloadLength} bytes passed the size check");
                    if (frame.IsControlFrame && (frame.PayloadLength > 125 || !frame.IsFinal))
                        throw new FuzzFindingException("oversized or fragmented control frame accepted");
                    if (!frame.IsControlFrame)
                        fragmented = !frame.IsFinal;

                    if (!assembler.TryAssemble(lease, out var message))
                        continue;

                    using (message)
                    {
                        if (message.Payload.Length > WsMaxMessageSize)
                            throw new FuzzFindingException($"message of {message.Payload.Length} bytes passed the limit");
                        if (message.IsRsv1Set)
                        {
                            using var inflated = deflate.TransformInbound(message.Payload, message.Opcode, message.RsvBits);
                            CheckInflated(inflated);
                        }
                    }
                }
            }
            catch (WebSocketException ex)
            {
                Expected(ex);
            }
            catch (WebSocketProtocolException ex)
            {
                Expected(ex);
            }
            catch (IOException ex)
            {
                Expected(ex);
            }
        }

        /// <summary>
        /// permessage-deflate inflate alone, several messages on one negotiated extension (each one
        /// inflated on a fresh context, server_no_context_takeover). Input: repeated [u16 BE length][payload];
        /// each payload is a compressed message body (RSV1 set, the 00 00 ff ff tail stripped).
        /// </summary>
        private static void WsInflate(byte[] data)
        {
            using var deflate = NegotiatedDeflate();
            int pos = 0;
            try
            {
                while (pos < data.Length)
                {
                    int length;
                    if (data.Length - pos < 2)
                    {
                        length = data.Length - pos;
                    }
                    else
                    {
                        length = Math.Min((data[pos] << 8) | data[pos + 1], data.Length - pos - 2);
                        pos += 2;
                    }

                    using var inflated = deflate.TransformInbound(
                        new ReadOnlyMemory<byte>(data, pos, length), WebSocketOpcode.Binary, 0x40);
                    CheckInflated(inflated);
                    pos += length;
                }
            }
            catch (WebSocketException ex)
            {
                Expected(ex);
            }
        }

        private static PerMessageDeflateExtension NegotiatedDeflate()
        {
            // Inbound context takeover is not implemented, so the client only accepts a server that
            // resets its context per message (PerMessageDeflateExtension.AcceptNegotiation).
            var deflate = new PerMessageDeflateExtension(null, WsMaxMessageSize);
            if (!deflate.AcceptNegotiation(new WebSocketExtensionParameters(
                    "permessage-deflate", new Dictionary<string, string> { ["server_no_context_takeover"] = null })))
            {
                deflate.Dispose();
                throw new FuzzFindingException("permessage-deflate rejected an empty server response");
            }

            return deflate;
        }

        private static void CheckInflated(System.Buffers.IMemoryOwner<byte> inflated)
        {
            if (inflated != null && inflated.Memory.Length > WsMaxMessageSize)
                throw new FuzzFindingException($"inflated {inflated.Memory.Length} bytes past the {WsMaxMessageSize}-byte limit");
        }

        // --- LiteJson (RFC 8259) ---

        /// <summary>The LiteJson reader: grammar, escapes, numbers, the nesting cap. Input: UTF-8 text.</summary>
        private static void Json(byte[] data)
        {
            try
            {
                new JsonReader(Encoding.UTF8.GetString(data)).Parse();
            }
            catch (JsonParseException ex)
            {
                Expected(ex);
            }
        }
    }

    /// <summary>
    /// Runs an async target to completion on the calling thread. The fuzz drivers call targets
    /// synchronously; this is a tool entry point, not library code.
    /// </summary>
    internal static class Sync
    {
        public static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    }
}
