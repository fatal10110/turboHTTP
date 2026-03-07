using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Manages a single HTTP/2 connection: initialization, request sending with HPACK headers + DATA,
    /// background frame reading/dispatch, flow control, stream multiplexing, and shutdown.
    /// RFC 7540 Sections 3.5, 4, 5, 6, 8.
    /// </summary>
    internal partial class Http2Connection : IDisposable
    {
        // Connection identity
        public string Host { get; }
        public int Port { get; }

        // I/O
        private readonly Stream _stream;
        private readonly Http2FrameCodec _codec;

        // HPACK (separate encoder/decoder, each with own dynamic table)
        private readonly HpackEncoder _hpackEncoder;
        private readonly HpackDecoder _hpackDecoder;
        // Reused across requests to avoid per-request List<> allocation.
        // Accessed only inside _writeLock — safe because HTTP/2 HEADERS frames are
        // encoded and sent serially (one HEADERS block at a time per connection).
        private readonly List<(string Name, string Value)> _headerListScratch =
            new List<(string Name, string Value)>(24);
        // Reused across responses to avoid per-response List<> allocation.
        // Accessed only from the single background ReadLoopAsync task — never shared
        // with send paths, so no lock is needed.
        private readonly List<(string Name, string Value)> _decodedHeaderScratch =
            new List<(string Name, string Value)>(24);
        // Current client transport negotiates HTTP/2 only over TLS, so :scheme is always https.
        // If h2c is introduced, this should be promoted to a transport-provided connection property.
        private readonly string _schemeHeader;

        // Stream management
        private readonly ConcurrentDictionary<int, Http2Stream> _activeStreams
            = new ConcurrentDictionary<int, Http2Stream>();
        private readonly object _streamCreateLock = new object();
        private int _nextStreamId = 1;
        private static readonly PoolableValueTaskSourcePool<UHttpResponse> s_responseSourcePool =
            new PoolableValueTaskSourcePool<UHttpResponse>(maxSize: 256);

        // Settings
        private readonly Http2Settings _localSettings = new Http2Settings();
        private readonly Http2Settings _remoteSettings = new Http2Settings();

        // Flow control
        private int _connectionSendWindow = Http2Constants.DefaultInitialWindowSize;
        private int _connectionRecvWindow = Http2Constants.DefaultInitialWindowSize;
        private readonly SemaphoreSlim _windowWaiter = new SemaphoreSlim(0);

        // Write serialization
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Lifecycle
        private Task _readLoopTask;
        private Task _keepAliveTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _goawayReceived;
        private int _lastGoawayStreamId;
        private int _disposed;
        private static readonly TimeSpan KeepAlivePingInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SettingsAckTimeout = TimeSpan.FromSeconds(5);

        // CONTINUATION tracking
        private int _continuationStreamId;

        // Initialization handshake
        private readonly ResettableValueTaskSource<bool> _settingsAckSource;

        // HPACK table size tracking (for detecting changes including back-to-default)
        private int _lastHeaderTableSize = Http2Constants.DefaultHeaderTableSize;

        public bool IsAlive =>
            !_goawayReceived &&
            !_cts.IsCancellationRequested &&
            _readLoopTask != null &&
            !_readLoopTask.IsCompleted;

        public Http2Connection(
            Stream stream,
            string host,
            int port,
            Http2Options options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Port = port;
            _codec = new Http2FrameCodec(stream);
            _hpackEncoder = new HpackEncoder();
            _hpackDecoder = new HpackDecoder(maxDecodedHeaderBytes: options.MaxDecodedHeaderBytes);
            _schemeHeader = "https";
            _localSettings = new Http2Settings(options);
            _localSettings.Apply(Http2SettingId.EnablePush, options.EnablePush ? 1u : 0u);
            _settingsAckSource = new ResettableValueTaskSource<bool>();
            _settingsAckSource.PrepareForUse();
        }

        // Backward-compatible constructor that uses default HTTP/2 options.
        public Http2Connection(Stream stream, string host, int port)
            : this(stream, host, port, new Http2Options())
        {
        }

        /// <summary>
        /// Initialize the HTTP/2 connection: send preface + SETTINGS, start read loop,
        /// wait for server SETTINGS ACK.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct)
        {
            _settingsAckSource.PrepareForUse();

            // 1. Write connection preface
            await _codec.WritePrefaceAsync(ct);

            // 2. Send initial SETTINGS frame
            byte[] settingsPayload = _localSettings.SerializeClientSettings();
            await _codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = settingsPayload,
                Length = settingsPayload.Length
            }, ct);

            // 3. Start background read loop
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));

            // 4. Wait for SETTINGS ACK with timeout
            using (var ackTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                ackTimeoutCts.CancelAfter(SettingsAckTimeout);
                using (ackTimeoutCts.Token.Register(() =>
                       {
                           if (ct.IsCancellationRequested)
                           {
                               try
                               {
                                   _settingsAckSource.SetCanceled(ct);
                               }
                               catch (InvalidOperationException)
                               {
                                   // ACK may have raced and completed the source concurrently.
                               }
                               return;
                           }

                           var timeoutError = new UHttpException(new UHttpError(
                               UHttpErrorType.Timeout,
                               "HTTP/2 SETTINGS ACK timeout"));
                           try
                           {
                               _settingsAckSource.SetException(timeoutError);
                           }
                           catch (InvalidOperationException)
                           {
                               // ACK may have raced and completed the source concurrently.
                           }
                       }))
                {
                    await _settingsAckSource.CreateValueTask().ConfigureAwait(false);
                }
            }

            // Keep idle HTTP/2 connections active on mobile NATs.
            if (_keepAliveTask == null)
                _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Send an HTTP/2 request and wait for the response.
        /// </summary>
        public async ValueTask<UHttpResponse> SendRequestAsync(
            UHttpRequest request, RequestContext context, CancellationToken ct)
        {
            Http2Stream stream = null;
            int streamId = -1;

            // Outer try/finally is the sole BodyOwner disposal site. This guarantees owner-backed
            // request bodies remain valid through flow-control waits in SendDataAsync.
            try
            {
                lock (_streamCreateLock)
                {
                    // Step 1: Pre-flight checks
                    if (_goawayReceived)
                        throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            "Connection received GOAWAY"));
                    if (_cts.IsCancellationRequested)
                        throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            "Connection is closed"));

                    // Step 2: Enforce SETTINGS_MAX_CONCURRENT_STREAMS (RFC 7540 Section 5.1.2)
                    if (_activeStreams.Count >= _remoteSettings.MaxConcurrentStreams)
                        throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            $"MAX_CONCURRENT_STREAMS limit reached ({_remoteSettings.MaxConcurrentStreams})"));

                    // Step 3: Allocate stream ID and reject IDs beyond GOAWAY last stream.
                    streamId = AllocateNextStreamId();
                    if (_goawayReceived || (_lastGoawayStreamId > 0 && streamId > _lastGoawayStreamId))
                    {
                        throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            "Connection closed during stream creation"));
                    }

                    // Step 4: Rent a pooled stream and initialise it for this request.
                    stream = Http2StreamPool.Rent(streamId, request, context,
                        _remoteSettings.InitialWindowSize,
                        _localSettings.InitialWindowSize,
                        s_responseSourcePool);
                    _activeStreams[streamId] = stream;
                }

                // Check if already cancelled
                if (ct.IsCancellationRequested)
                {
                    _activeStreams.TryRemove(streamId, out _);
                    stream.CancelWithoutConsumption(ct);
                    Http2StreamPool.Return(stream);
                    stream = null;
                    throw new OperationCanceledException(ct);
                }

                // Register per-request cancellation.
                // RFC 7540 Section 8.1: when cancellation fires mid-send, RST_STREAM and
                // any in-flight DATA frames may be delivered out of order. This is acceptable
                // per the RFC — the peer will reset the stream on receipt of RST_STREAM
                // regardless of DATA frames that arrive before it.
                stream.CancellationRegistration = ct.Register(() =>
                {
                    if (_activeStreams.TryRemove(streamId, out var canceledStream))
                    {
                        _ = SendRstStreamAsync(streamId, Http2ErrorCode.Cancel);
                        canceledStream.Cancel();
                    }
                });

                try
                {
                    bool hasBody = !request.Body.IsEmpty;

                    // Step 6: HPACK encode headers + send HEADERS (under write lock)
                    await _writeLock.WaitAsync(ct);
                    try
                    {
                        _headerListScratch.Clear();
                        _headerListScratch.Add((":method", request.Method.ToUpperString()));
                        _headerListScratch.Add((":scheme", _schemeHeader));
                        _headerListScratch.Add((":authority", BuildAuthorityValue(request.Uri)));
                        _headerListScratch.Add((":path", request.Uri.PathAndQuery ?? "/"));

                        foreach (var name in request.Headers.Names)
                        {
                            if (IsHttp2ForbiddenHeader(name))
                                continue;

                            // RFC 7540 Section 8.1.2.2: te header is forbidden in HTTP/2
                            // except with the value "trailers".
                            if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase))
                            {
                                var teValues = request.Headers.GetValues(name);
                                foreach (var v in teValues)
                                {
                                    if (string.Equals(v.Trim(), "trailers", StringComparison.OrdinalIgnoreCase))
                                        _headerListScratch.Add(("te", "trailers"));
                                }
                                continue;
                            }

                            var lowerName = ToLowerAsciiHeaderName(name);
                            foreach (var value in request.Headers.GetValues(name))
                                _headerListScratch.Add((lowerName, value));
                        }

                        if (!request.Headers.Contains("user-agent"))
                            _headerListScratch.Add(("user-agent", "TurboHTTP/1.0"));

                        // INVARIANT: headerBlock is a direct slice of HpackEncoder._outputBuffer and is
                        // only valid until the next Encode() call. SendHeadersAsync MUST be awaited
                        // before releasing _writeLock, which prevents any concurrent Encode() call from
                        // resetting the backing buffer while the HEADERS frame is being written.
                        var headerBlock = _hpackEncoder.Encode(_headerListScratch);

                        await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct);

                        stream.State = hasBody ? Http2StreamState.Open : Http2StreamState.HalfClosedLocal;
                    }
                    finally
                    {
                        _writeLock.Release();
                    }

                    // Step 7: Send DATA frames OUTSIDE write lock
                    if (!request.Body.IsEmpty)
                    {
                        await SendDataAsync(streamId, request.Body, stream, ct);
                        stream.State = Http2StreamState.HalfClosedLocal;
                    }

                    context.RecordEvent("TransportH2RequestSent");

                    // Step 8: Wait for response
                    return await stream.ResponseTask.ConfigureAwait(false);
                }
                catch (ObjectDisposedException ex)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "HTTP/2 connection disposed during request send.",
                        ex));
                }
                finally
                {
                    if (stream != null)
                    {
                        _activeStreams.TryRemove(streamId, out _);
                        try
                        {
                            if (stream.IsResponseCompleted)
                            {
                                stream.ReturnResponseSourceIfUnconsumed();
                            }
                            else
                            {
                                stream.CancelWithoutConsumption(ct);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            stream.ReturnResponseSourceIfUnconsumed();
                        }

                        // Return the stream to the pool. The pool's reset delegate calls
                        // PrepareForPool() which clears per-request references and returns
                        // any partial body buffer to ArrayPool.
                        Http2StreamPool.Return(stream);
                    }
                }
            }
            finally
            {
                // Dispose BodyOwner on all exit paths (GOAWAY/limit exceptions, cancellation, send
                // failures, and success). This is intentionally the only disposal site for owner-backed
                // bodies used by zero-copy request transport.
                request.DisposeBodyOwner();
            }
        }

        private static string ToLowerAsciiHeaderName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 'A' && c <= 'Z')
                {
                    return string.Create(value.Length, value, static (destination, source) =>
                    {
                        for (int j = 0; j < source.Length; j++)
                        {
                            char ch = source[j];
                            destination[j] = ch >= 'A' && ch <= 'Z' ? (char)(ch | 0x20) : ch;
                        }
                    });
                }
            }

            return value;
        }
    }
}
