using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

// Shared by the NUnit suite (Http2AdversarialServerTests, host-editor run) and the plain .NET
// h2-interop harness (Tools~/h2-interop, PR CI), which links this folder with <Compile Include>.
// Nothing here may reference NUnit or the Unity engine.
namespace TurboHTTP.Tests.Transport.Http2.Adversarial
{
    /// <summary>A scripted adversarial scenario did not observe the expected client behavior.</summary>
    internal sealed class Http2ScenarioException : Exception
    {
        public Http2ScenarioException(string message) : base(message) { }

        public Http2ScenarioException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Framework-neutral assertions for the scenarios (NUnit is not available in the harness).</summary>
    internal static class ScenarioAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
                throw new Http2ScenarioException(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Http2ScenarioException(what + ": expected <" + expected + "> but was <" + actual + ">");
        }

        /// <summary>Awaits <paramref name="task"/> and returns the exception, which must be (or derive from) <typeparamref name="TException"/>.</summary>
        public static async Task<TException> ThrowsAsync<TException>(Task task, string what)
            where TException : Exception
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new Http2ScenarioException(
                    what + ": expected " + typeof(TException).Name + " but got " + ex.GetType().Name + ": " + ex.Message, ex);
            }

            throw new Http2ScenarioException(what + ": expected " + typeof(TException).Name + " but the task completed");
        }
    }

    /// <summary>
    /// Raw HPACK field-block builder (RFC 7541 Section 6) for frames a well-behaved encoder would never
    /// produce: exact dynamic-table insertions, references, size updates anywhere in the block.
    /// Strings are never Huffman-coded.
    /// </summary>
    internal sealed class HpackBlock
    {
        /// <summary>The first dynamic-table index (RFC 7541 Section 2.3.3).</summary>
        public const int FirstDynamicIndex = 62;

        private readonly List<byte> _bytes = new List<byte>();

        /// <summary>Indexed field (Section 6.1).</summary>
        public HpackBlock Indexed(int index)
        {
            WriteInteger(index, 7, 0x80);
            return this;
        }

        /// <summary>:status, from the static table when it has the code, else a literal without indexing.</summary>
        public HpackBlock Status(int status)
        {
            int index = status switch
            {
                200 => 8,
                204 => 9,
                206 => 10,
                304 => 11,
                400 => 12,
                404 => 13,
                500 => 14,
                _ => 0
            };
            if (index != 0)
                return Indexed(index);

            WriteInteger(8, 4, 0x00);
            WriteString(status.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>Literal with incremental indexing, new name (Section 6.2.1): inserts into the dynamic table.</summary>
        public HpackBlock LiteralIncremental(string name, string value)
        {
            _bytes.Add(0x40);
            WriteString(name);
            WriteString(value);
            return this;
        }

        /// <summary>Literal without indexing, new name (Section 6.2.2): leaves the dynamic table alone.</summary>
        public HpackBlock Literal(string name, string value)
        {
            _bytes.Add(0x00);
            WriteString(name);
            WriteString(value);
            return this;
        }

        /// <summary>Dynamic table size update (Section 6.3).</summary>
        public HpackBlock SizeUpdate(int maxSize)
        {
            WriteInteger(maxSize, 5, 0x20);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();

        private void WriteInteger(int value, int prefixBits, byte pattern)
        {
            int max = (1 << prefixBits) - 1;
            if (value < max)
            {
                _bytes.Add((byte)(pattern | value));
                return;
            }

            _bytes.Add((byte)(pattern | max));
            value -= max;
            while (value >= 128)
            {
                _bytes.Add((byte)((value % 128) + 128));
                value /= 128;
            }

            _bytes.Add((byte)value);
        }

        private void WriteString(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            WriteInteger(bytes.Length, 7, 0x00);
            _bytes.AddRange(bytes);
        }
    }

    /// <summary>
    /// A frame-level, single-threaded scripted HTTP/2 server driving a real <see cref="Http2Connection"/>
    /// over an in-memory duplex stream. The scenario owns every write and every read: frames are read only
    /// while the scenario awaits <see cref="WaitForAsync"/>, which folds each client frame into the
    /// observed state below (request header blocks are always HPACK-decoded in order, client DATA is
    /// checked against the windows this server advertised). No wall clock: every wait is bounded by the
    /// caller's watchdog token.
    /// </summary>
    internal sealed class ScriptedHttp2Server : IDisposable
    {
        public const string Authority = "test.example.com";
        private const int MaxFrame = Http2Constants.DefaultMaxFrameSize;

        private readonly TestDuplexStream _duplex;
        private readonly HpackDecoder _requestDecoder = new HpackDecoder();
        private readonly Queue<int> _pendingInitialWindows = new Queue<int>();
        private readonly Dictionary<int, long> _streamSendWindows = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _streamRecvWindows = new Dictionary<int, long>();
        private MemoryStream _headerBlock;
        private int _headerBlockStream;
        private bool _headerBlockEndStream;
        private int _clientInitialWindow = Http2Constants.DefaultInitialWindowSize;
        private int _serverInitialWindow = Http2Constants.DefaultInitialWindowSize;
        private int _latestServerInitialWindow = Http2Constants.DefaultInitialWindowSize;
        private long _recvConnectionWindow = Http2Constants.DefaultInitialWindowSize;

        private ScriptedHttp2Server(Http2Options options)
        {
            _duplex = new TestDuplexStream();
            Client = new Http2Connection(_duplex.ClientStream, Authority, 443, options ?? new Http2Options(), new StreamingOptions());
            Codec = new Http2FrameCodec(_duplex.ServerStream);
        }

        public Http2Connection Client { get; }

        public Http2FrameCodec Codec { get; }

        /// <summary>Request streams in the order their header blocks completed.</summary>
        public List<int> RequestStreams { get; } = new List<int>();

        /// <summary>Decoded request fields per stream.</summary>
        public Dictionary<int, List<(string Name, string Value)>> RequestHeaders { get; } =
            new Dictionary<int, List<(string Name, string Value)>>();

        public Dictionary<int, MemoryStream> RequestBodies { get; } = new Dictionary<int, MemoryStream>();

        /// <summary>Streams the client half-closed (END_STREAM on HEADERS or DATA).</summary>
        public HashSet<int> EndedByClient { get; } = new HashSet<int>();

        /// <summary>RST_STREAM frames from the client, by stream.</summary>
        public Dictionary<int, Http2ErrorCode> ResetByClient { get; } = new Dictionary<int, Http2ErrorCode>();

        public List<(int LastStreamId, Http2ErrorCode Code)> GoAways { get; } = new List<(int, Http2ErrorCode)>();

        public List<long> PingAcks { get; } = new List<long>();

        public int SettingsAcks { get; private set; }

        /// <summary>Server SETTINGS frames the client has not acknowledged yet.</summary>
        public int SettingsPending => _pendingInitialWindows.Count;

        /// <summary>Highest request stream id seen; client ids must increase (RFC 9113 Section 5.1.1).</summary>
        public int HighestStreamId { get; private set; }

        public bool Disconnected { get; private set; }

        /// <summary>Called for every completed request header block, before it is added to <see cref="RequestStreams"/>.</summary>
        public Action<int> OnRequestHeaders { get; set; }

        /// <summary>Server send credit (connection level) granted by the client's WINDOW_UPDATE frames.</summary>
        public long ConnectionSendWindow { get; private set; } = Http2Constants.DefaultInitialWindowSize;

        /// <summary>Completes the preface and SETTINGS exchange. <paramref name="settings"/> go into the server's SETTINGS frame.</summary>
        public static async Task<ScriptedHttp2Server> StartAsync(
            CancellationToken ct,
            Http2Options options = null,
            params (Http2SettingId Id, uint Value)[] settings)
        {
            var server = new ScriptedHttp2Server(options);
            try
            {
                var handshake = Task.Run(() => server.HandshakeAsync(settings, ct), ct);
                await server.Client.InitializeAsync(ct).ConfigureAwait(false);
                await handshake.ConfigureAwait(false);
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        private async Task HandshakeAsync((Http2SettingId Id, uint Value)[] settings, CancellationToken ct)
        {
            var preface = new byte[Http2Constants.ConnectionPreface.Length];
            int read = 0;
            while (read < preface.Length)
            {
                int n = await _duplex.ServerStream.ReadAsync(preface, read, preface.Length - read, ct).ConfigureAwait(false);
                if (n == 0)
                    throw new Http2ScenarioException("client closed before the preface");
                read += n;
            }

            ScenarioAssert.Equal(Encoding.ASCII.GetString(Http2Constants.ConnectionPreface), Encoding.ASCII.GetString(preface), "client preface");
            var clientSettings = await Codec.ReadFrameAsync(MaxFrame, ct).ConfigureAwait(false);
            ScenarioAssert.True(clientSettings.Type == Http2FrameType.Settings && !clientSettings.HasFlag(Http2FrameFlags.Ack),
                "the first client frame must be SETTINGS (RFC 9113 Section 3.4)");
            for (int i = 0; i + 6 <= clientSettings.Length; i += 6)
            {
                var id = (Http2SettingId)((clientSettings.Payload[i] << 8) | clientSettings.Payload[i + 1]);
                uint value = ReadUInt32(clientSettings.Payload, i + 2);
                if (id == Http2SettingId.InitialWindowSize)
                    _clientInitialWindow = (int)value;
            }

            // Nothing was sent under the default window yet: the advertised value applies from the start.
            foreach (var (id, value) in settings)
            {
                if (id == Http2SettingId.InitialWindowSize)
                    _serverInitialWindow = _latestServerInitialWindow = (int)value;
            }

            _pendingInitialWindows.Enqueue(_latestServerInitialWindow);
            await WriteFrameAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, SettingsPayload(settings), ct).ConfigureAwait(false);
            await WriteFrameAsync(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, Array.Empty<byte>(), ct).ConfigureAwait(false);
        }

        // ---- client side ----------------------------------------------------------------------------

        /// <summary>Sends a request through <see cref="Http2Connection.DispatchAsync"/> and buffers the response.</summary>
        public Task<UHttpResponse> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken ct)
        {
            var request = new UHttpRequest(method, new Uri("https://" + Authority + path), body: body);
            return TransportDispatchHelper.CollectResponseAsync(Client.DispatchAsync, request, new RequestContext(request), ct);
        }

        public Task<UHttpResponse> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.GET, path, null, ct);

        public string PathOf(int streamId)
        {
            foreach (var (name, value) in RequestHeaders[streamId])
            {
                if (name == ":path")
                    return value;
            }

            return null;
        }

        /// <summary>The stream that carried <paramref name="path"/>, or 0 when the client never sent it.</summary>
        public int StreamOf(string path)
        {
            foreach (int id in RequestStreams)
            {
                if (PathOf(id) == path)
                    return id;
            }

            return 0;
        }

        // ---- reading ----------------------------------------------------------------------------------

        /// <summary>Reads client frames until <paramref name="condition"/> holds. Fails if the client disconnects first.</summary>
        public async Task WaitForAsync(Func<bool> condition, string what, CancellationToken ct)
        {
            while (!condition())
            {
                if (Disconnected)
                    throw new Http2ScenarioException("client closed the connection while waiting for " + what);
                try
                {
                    await ReadOneAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                {
                    throw new Http2ScenarioException("watchdog expired while waiting for " + what, ex);
                }
            }
        }

        public async Task<int> NextRequestAsync(CancellationToken ct)
        {
            int count = RequestStreams.Count;
            await WaitForAsync(() => RequestStreams.Count > count, "a request", ct).ConfigureAwait(false);
            return RequestStreams[count];
        }

        public async Task<(int LastStreamId, Http2ErrorCode Code)> ExpectGoAwayAsync(CancellationToken ct)
        {
            await WaitForAsync(() => GoAways.Count > 0, "GOAWAY", ct).ConfigureAwait(false);
            return GoAways[0];
        }

        public async Task<Http2ErrorCode> ExpectResetAsync(int streamId, CancellationToken ct)
        {
            await WaitForAsync(() => ResetByClient.ContainsKey(streamId), "RST_STREAM on " + streamId, ct).ConfigureAwait(false);
            return ResetByClient[streamId];
        }

        /// <summary>A PING round trip: every client frame written before the client read the PING is folded in on return.</summary>
        public async Task SyncAsync(long opaque, CancellationToken ct)
        {
            await WritePingAsync(opaque, ack: false, ct).ConfigureAwait(false);
            await WaitForAsync(() => PingAcks.Contains(opaque), "PING ACK " + opaque, ct).ConfigureAwait(false);
        }

        private async Task ReadOneAsync(CancellationToken ct)
        {
            Http2Frame frame;
            try
            {
                frame = await Codec.ReadFrameAsync(MaxFrame, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                Disconnected = true;
                return;
            }

            if (frame == null)
            {
                Disconnected = true;
                return;
            }

            await ObserveAsync(frame, ct).ConfigureAwait(false);
        }

        private async Task ObserveAsync(Http2Frame frame, CancellationToken ct)
        {
            if (_headerBlock != null && (frame.Type != Http2FrameType.Continuation || frame.StreamId != _headerBlockStream))
                throw new Http2ScenarioException("client interleaved " + frame.Type + " inside a header block (RFC 9113 Section 4.3)");

            switch (frame.Type)
            {
                case Http2FrameType.Headers:
                    ObserveHeaders(frame);
                    break;
                case Http2FrameType.Continuation:
                    ScenarioAssert.True(_headerBlock != null, "client CONTINUATION without HEADERS");
                    _headerBlock.Write(frame.Payload, 0, frame.Length);
                    if (frame.HasFlag(Http2FrameFlags.EndHeaders))
                        CompleteHeaderBlock();
                    break;
                case Http2FrameType.Data:
                    ObserveData(frame);
                    break;
                case Http2FrameType.WindowUpdate:
                    long increment = ReadUInt32(frame.Payload, 0) & 0x7FFFFFFF;
                    ScenarioAssert.True(increment > 0, "client WINDOW_UPDATE with a zero increment");
                    if (frame.StreamId == 0)
                        ConnectionSendWindow += increment;
                    else
                        _streamSendWindows[frame.StreamId] = StreamSendWindow(frame.StreamId) + increment;
                    break;
                case Http2FrameType.RstStream:
                    ResetByClient[frame.StreamId] = (Http2ErrorCode)ReadUInt32(frame.Payload, 0);
                    break;
                case Http2FrameType.Settings:
                    if (frame.HasFlag(Http2FrameFlags.Ack))
                    {
                        SettingsAcks++;
                        if (_pendingInitialWindows.Count > 0)
                            ApplyServerInitialWindow(_pendingInitialWindows.Dequeue());
                    }
                    else
                    {
                        await WriteFrameAsync(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, Array.Empty<byte>(), ct).ConfigureAwait(false);
                    }

                    break;
                case Http2FrameType.Ping:
                    if (frame.HasFlag(Http2FrameFlags.Ack))
                        PingAcks.Add(ReadInt64(frame.Payload));
                    else
                        await WritePingAsync(ReadInt64(frame.Payload), ack: true, ct).ConfigureAwait(false);
                    break;
                case Http2FrameType.GoAway:
                    GoAways.Add(((int)(ReadUInt32(frame.Payload, 0) & 0x7FFFFFFF), (Http2ErrorCode)ReadUInt32(frame.Payload, 4)));
                    break;
            }
        }

        private void ObserveHeaders(Http2Frame frame)
        {
            int offset = 0;
            int length = frame.Length;
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                int pad = frame.Payload[0];
                offset = 1;
                length -= 1 + pad;
            }

            if (frame.HasFlag(Http2FrameFlags.HasPriority))
            {
                offset += 5;
                length -= 5;
            }

            ScenarioAssert.True(frame.StreamId % 2 == 1, "client opened even stream " + frame.StreamId);
            ScenarioAssert.True(frame.StreamId > HighestStreamId,
                "client stream id " + frame.StreamId + " after " + HighestStreamId + " (RFC 9113 Section 5.1.1)");
            HighestStreamId = frame.StreamId;
            _headerBlock = new MemoryStream();
            _headerBlock.Write(frame.Payload, offset, length);
            _headerBlockStream = frame.StreamId;
            _headerBlockEndStream = frame.HasFlag(Http2FrameFlags.EndStream);
            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
                CompleteHeaderBlock();
        }

        private void CompleteHeaderBlock()
        {
            var block = _headerBlock.ToArray();
            int id = _headerBlockStream;
            _headerBlock = null;
            RequestHeaders[id] = _requestDecoder.Decode(block, 0, block.Length);
            RequestBodies[id] = new MemoryStream();
            _streamRecvWindows[id] = _serverInitialWindow;
            if (_headerBlockEndStream)
                EndedByClient.Add(id);
            OnRequestHeaders?.Invoke(id);
            RequestStreams.Add(id);
        }

        private void ObserveData(Http2Frame frame)
        {
            int id = frame.StreamId;
            ScenarioAssert.True(RequestBodies.ContainsKey(id), "client DATA on unopened stream " + id);
            ScenarioAssert.True(!EndedByClient.Contains(id), "client DATA after END_STREAM on " + id);
            _recvConnectionWindow -= frame.Length;
            _streamRecvWindows[id] -= frame.Length;
            ScenarioAssert.True(_recvConnectionWindow >= 0,
                "client DATA exceeds the connection window by " + (-_recvConnectionWindow) + " bytes (RFC 9113 Section 6.9)");
            ScenarioAssert.True(_streamRecvWindows[id] >= 0,
                "client DATA exceeds the window of stream " + id + " by " + (-_streamRecvWindows[id]) + " bytes (RFC 9113 Section 6.9)");
            RequestBodies[id].Write(frame.Payload, 0, frame.Length);
            if (frame.HasFlag(Http2FrameFlags.EndStream))
                EndedByClient.Add(id);
        }

        private void ApplyServerInitialWindow(int value)
        {
            int delta = value - _serverInitialWindow;
            _serverInitialWindow = value;
            var ids = new List<int>(_streamRecvWindows.Keys);
            foreach (int id in ids)
                _streamRecvWindows[id] += delta;
        }

        public long StreamSendWindow(int streamId) =>
            _streamSendWindows.TryGetValue(streamId, out long window) ? window : _clientInitialWindow;

        // ---- writing ----------------------------------------------------------------------------------

        public Task WriteFrameAsync(Http2FrameType type, Http2FrameFlags flags, int streamId, byte[] payload, CancellationToken ct)
        {
            return Codec.WriteFrameAsync(new Http2Frame
            {
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Payload = payload,
                Length = payload.Length
            }, ct);
        }

        /// <summary>HEADERS, split into CONTINUATION frames of <paramref name="fragment"/> bytes when it is positive.</summary>
        public async Task WriteHeadersAsync(int streamId, byte[] block, bool endStream, CancellationToken ct, int fragment = 0)
        {
            var endStreamFlag = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
            if (fragment <= 0 || block.Length <= fragment)
            {
                await WriteFrameAsync(Http2FrameType.Headers, endStreamFlag | Http2FrameFlags.EndHeaders, streamId, block, ct).ConfigureAwait(false);
                return;
            }

            for (int offset = 0; offset < block.Length; offset += fragment)
            {
                int length = Math.Min(fragment, block.Length - offset);
                var part = new byte[length];
                Buffer.BlockCopy(block, offset, part, 0, length);
                var type = offset == 0 ? Http2FrameType.Headers : Http2FrameType.Continuation;
                var flags = offset == 0 ? endStreamFlag : Http2FrameFlags.None;
                if (offset + length == block.Length)
                    flags |= Http2FrameFlags.EndHeaders;
                await WriteFrameAsync(type, flags, streamId, part, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// DATA within the send windows the client granted; blocks (reading client frames) for WINDOW_UPDATE
        /// when they are exhausted. <paramref name="ignoreStreamWindow"/> is for streams the client reset,
        /// which never get stream credit again but still count against the connection window.
        /// </summary>
        public async Task WriteDataAsync(int streamId, byte[] data, bool endStream, CancellationToken ct, bool ignoreStreamWindow = false)
        {
            int offset = 0;
            do
            {
                long credit = ignoreStreamWindow ? int.MaxValue : StreamSendWindow(streamId);
                int length = (int)Math.Min(Math.Min(MaxFrame, data.Length - offset), Math.Min(ConnectionSendWindow, credit));
                if (length <= 0 && offset < data.Length)
                {
                    await WaitForAsync(
                        () => ConnectionSendWindow > 0 && (ignoreStreamWindow || StreamSendWindow(streamId) > 0),
                        "WINDOW_UPDATE for stream " + streamId, ct).ConfigureAwait(false);
                    continue;
                }

                var part = new byte[length];
                Buffer.BlockCopy(data, offset, part, 0, length);
                offset += length;
                ConnectionSendWindow -= length;
                _streamSendWindows[streamId] = StreamSendWindow(streamId) - length;
                bool last = offset == data.Length;
                await WriteFrameAsync(Http2FrameType.Data, last && endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                    streamId, part, ct).ConfigureAwait(false);
            }
            while (offset < data.Length);
        }

        /// <summary>A complete response: literal fields (no dynamic-table insertions) and an optional body.</summary>
        public async Task RespondAsync(int streamId, int status, string body, CancellationToken ct, params (string Name, string Value)[] fields)
        {
            var block = new HpackBlock().Status(status);
            foreach (var (name, value) in fields)
                block.Literal(name, value);
            bool hasBody = !string.IsNullOrEmpty(body);
            await WriteHeadersAsync(streamId, block.ToArray(), endStream: !hasBody, ct).ConfigureAwait(false);
            if (hasBody)
                await WriteDataAsync(streamId, Encoding.ASCII.GetBytes(body), endStream: true, ct).ConfigureAwait(false);
        }

        /// <summary>SETTINGS; an INITIAL_WINDOW_SIZE in it is applied to the receive-window checks when the client ACKs.</summary>
        public Task WriteSettingsAsync(CancellationToken ct, params (Http2SettingId Id, uint Value)[] settings)
        {
            foreach (var (id, value) in settings)
            {
                if (id == Http2SettingId.InitialWindowSize)
                    _latestServerInitialWindow = (int)value;
            }

            _pendingInitialWindows.Enqueue(_latestServerInitialWindow);
            return WriteFrameAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, SettingsPayload(settings), ct);
        }

        public Task WriteGoAwayAsync(int lastStreamId, Http2ErrorCode code, CancellationToken ct)
        {
            var payload = new byte[8];
            WriteUInt32(payload, 0, (uint)lastStreamId);
            WriteUInt32(payload, 4, (uint)code);
            return WriteFrameAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, ct);
        }

        public Task WriteResetAsync(int streamId, Http2ErrorCode code, CancellationToken ct)
        {
            var payload = new byte[4];
            WriteUInt32(payload, 0, (uint)code);
            return WriteFrameAsync(Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, ct);
        }

        /// <summary>Grants the client send credit; the receive-window checks follow it.</summary>
        public Task WriteWindowUpdateAsync(int streamId, int increment, CancellationToken ct)
        {
            if (streamId == 0)
                _recvConnectionWindow += increment;
            else
                _streamRecvWindows[streamId] += increment;
            var payload = new byte[4];
            WriteUInt32(payload, 0, (uint)increment);
            return WriteFrameAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload, ct);
        }

        public Task WritePingAsync(long opaque, bool ack, CancellationToken ct)
        {
            var payload = new byte[8];
            WriteUInt32(payload, 0, (uint)(opaque >> 32));
            WriteUInt32(payload, 4, (uint)opaque);
            return WriteFrameAsync(Http2FrameType.Ping, ack ? Http2FrameFlags.Ack : Http2FrameFlags.None, 0, payload, ct);
        }

        public void Dispose()
        {
            Client.Dispose();
            _duplex.ServerStream.Dispose();
            _duplex.ClientStream.Dispose();
        }

        private static byte[] SettingsPayload((Http2SettingId Id, uint Value)[] settings)
        {
            var payload = new byte[settings.Length * 6];
            for (int i = 0; i < settings.Length; i++)
            {
                payload[i * 6] = (byte)((ushort)settings[i].Id >> 8);
                payload[i * 6 + 1] = (byte)settings[i].Id;
                WriteUInt32(payload, i * 6 + 2, settings[i].Value);
            }

            return payload;
        }

        private static uint ReadUInt32(byte[] b, int offset) =>
            ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

        private static long ReadInt64(byte[] b) => ((long)ReadUInt32(b, 0) << 32) | ReadUInt32(b, 4);

        private static void WriteUInt32(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)(value >> 24);
            b[offset + 1] = (byte)(value >> 16);
            b[offset + 2] = (byte)(value >> 8);
            b[offset + 3] = (byte)value;
        }
    }
}
