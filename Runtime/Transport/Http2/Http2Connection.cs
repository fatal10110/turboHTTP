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
            Http2Stream stream;
            int streamId;

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

                // Step 4: Create stream object (with both send and recv windows)
                stream = new Http2Stream(streamId, request, context,
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
                stream.Dispose();
                throw new OperationCanceledException(ct);
            }

            // Register per-request cancellation
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
                // Step 4: Build pseudo-headers + regular headers
                int headerValueCount = 5; // 4 pseudo-headers + user-agent fallback
                foreach (var name in request.Headers.Names)
                {
                    headerValueCount += request.Headers.GetValues(name).Count;
                }

                var headerList = new List<(string, string)>(headerValueCount);
                headerList.Add((":method", request.Method.ToUpperString()));
                headerList.Add((":scheme", ToLowerAsciiInvariant(request.Uri.Scheme)));
                headerList.Add((":authority", BuildAuthorityValue(request.Uri)));
                headerList.Add((":path", request.Uri.PathAndQuery ?? "/"));

                foreach (var name in request.Headers.Names)
                {
                    if (IsHttp2ForbiddenHeader(name)) continue;

                    // RFC 7540 Section 8.1.2.2: te header is forbidden in HTTP/2
                    // except with the value "trailers".
                    if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase))
                    {
                        var teValues = request.Headers.GetValues(name);
                        foreach (var v in teValues)
                        {
                            if (string.Equals(v.Trim(), "trailers", StringComparison.OrdinalIgnoreCase))
                                headerList.Add(("te", "trailers"));
                        }
                        continue;
                    }

                    foreach (var value in request.Headers.GetValues(name))
                        headerList.Add((ToLowerAsciiInvariant(name), value));
                }

                if (!request.Headers.Contains("user-agent"))
                    headerList.Add(("user-agent", "TurboHTTP/1.0"));

                // Step 5: HPACK encode headers + send HEADERS (under write lock)
                await _writeLock.WaitAsync(ct);
                try
                {
                    byte[] headerBlock = _hpackEncoder.Encode(headerList);
                    bool hasBody = request.Body != null && request.Body.Length > 0;

                    await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct);

                    stream.State = hasBody ? Http2StreamState.Open : Http2StreamState.HalfClosedLocal;
                }
                finally
                {
                    _writeLock.Release();
                }

                // Step 7: Send DATA frames OUTSIDE write lock
                bool hasBody2 = request.Body != null && request.Body.Length > 0;
                if (hasBody2)
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

                stream.Dispose();
            }
        }

        private static string ToLowerAsciiInvariant(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 'A' && c <= 'Z')
                    return value.ToLowerInvariant();
            }

            return value;
        }
    }
}
