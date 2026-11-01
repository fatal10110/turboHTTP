using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Manages a single HTTP/2 connection: initialization, request sending with HPACK headers + DATA,
    /// background frame reading/dispatch, flow control, stream multiplexing, and shutdown.
    /// RFC 7540 Sections 3.5, 4, 5, 6, 8.
    /// </summary>
    internal partial class Http2Connection : IDisposable
    {
        private const int DefaultConnectionReceiveWindowTargetBytes = 1024 * 1024;
        private const int RecentlyResetStreamSoftLimit = 512;
        private const int RecentlyResetStreamHardLimit = 1024;
        private static readonly TimeSpan DefaultMaintenanceInterval = TimeSpan.FromSeconds(5);
        private static readonly Action<object> s_requestCancellationCallback = static state =>
        {
            ((Http2Stream)state).HandleRequestCancellation();
        };

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

        // Settings
        private readonly Http2Settings _localSettings = new Http2Settings();
        private readonly Http2Settings _remoteSettings = new Http2Settings();

        // Flow control
        private int _connectionSendWindow = Http2Constants.DefaultInitialWindowSize;
        private int _connectionRecvWindow = Http2Constants.DefaultInitialWindowSize;
        // Send-window waiters (RFC 9113 Section 6.9, 6.9.2): one broadcast signal per connection, created
        // lazily by the first sender that blocks. Every event that can let a blocked sender continue
        // completes and clears it (WakeSendWindowWaiters); each waiter then re-checks its own windows,
        // stream completion and cancellation. Accessed only through Volatile/Interlocked.
        private TaskCompletionSource<bool> _sendWindowSignal;
        private readonly SemaphoreSlim _connectionRecvWindowUpdateLock = new SemaphoreSlim(1, 1);
        private long _connectionBufferedBytes;
        private int _connectionWindowUpdateScheduled;

        // Write serialization
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Lifecycle
        private Task _readLoopTask;
        private Task _keepAliveTask;
        private Task _maintenanceTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _goawayReceived;
        private int _lastGoawayStreamId;
        private int _disposed;
        private int _resourcesDisposed;
        private static readonly TimeSpan KeepAlivePingInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SettingsAckTimeout = TimeSpan.FromSeconds(5);

        // Field-block reception (RFC 9113 Section 4.3, 6.10). Owned by the single ReadLoopAsync task:
        // HEADERS/PUSH_PROMISE + CONTINUATION frames are contiguous on the wire, so at most one block
        // is open per connection. It accumulates here, independent of _activeStreams, so a block for a
        // closed, cancelled or reset stream is still HPACK-decoded and the dynamic table stays in sync.
        // _continuationStreamId is the stream of the open block (0 = none).
        private int _continuationStreamId;
        private byte[] _headerBlockBuffer; // rented per multi-frame block, returned at END_HEADERS or loop exit
        private int _headerBlockLength;
        private int _headerBlockContinuationCount;
        private bool _headerBlockEndStream;
        private bool _headerBlockIsPushPromise;

        // CONTINUATION flood bounds (RFC 9113 Section 10.5.1). Exceeding either is ENHANCE_YOUR_CALM.
        internal const int MaxHeaderBlockBytesCeiling = 256 * 1024;
        internal const int MaxContinuationFramesPerHeaderBlock = 128;

        // Initialization handshake
        private readonly ResettableValueTaskSource<bool> _settingsAckSource;

        private readonly int _perStreamReceiveBufferBytes;
        private readonly int _streamingSendBufferBytes;
        private readonly int _maxConnectionBufferedBytes;
        private readonly int _connectionRecvWindowTarget;
        private readonly TimeSpan _maintenanceInterval;
        private readonly int _expectContinueTimeoutMs;
        private long _stallTimeoutMs;
        private readonly ConcurrentDictionary<int, long> _recentlyResetStreams =
            new ConcurrentDictionary<int, long>();
        private int _streamDisposed;

        internal int PerStreamReceiveBufferBytes => _perStreamReceiveBufferBytes;
        internal int StreamingSendBufferBytes => _streamingSendBufferBytes;
        internal int MaxConnectionBufferedBytes => _maxConnectionBufferedBytes;
        internal long StallTimeoutMilliseconds => Volatile.Read(ref _stallTimeoutMs);

        public bool IsAlive =>
            !_goawayReceived &&
            !_cts.IsCancellationRequested &&
            _readLoopTask != null &&
            !_readLoopTask.IsCompleted;

        public Http2Connection(
            Stream stream,
            string host,
            int port,
            Http2Options options,
            StreamingOptions streamingOptions)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (streamingOptions == null)
            {
                throw new ArgumentNullException(nameof(streamingOptions));
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
            _streamingSendBufferBytes = streamingOptions.DefaultStreamingSendBufferBytes;
            _perStreamReceiveBufferBytes = options.TestPerStreamReceiveBufferBytesOverride > 0
                ? options.TestPerStreamReceiveBufferBytesOverride
                : Math.Max(
                    streamingOptions.DefaultHttp2PerStreamReceiveBufferBytes,
                    Math.Max(_localSettings.InitialWindowSize, _localSettings.MaxFrameSize));
            _maxConnectionBufferedBytes = options.TestMaxConnectionBufferedBytesOverride > 0
                ? options.TestMaxConnectionBufferedBytesOverride
                : streamingOptions.MaxConnectionBufferedBytes;
            _connectionRecvWindowTarget = Math.Max(
                Http2Constants.DefaultInitialWindowSize,
                Math.Min(DefaultConnectionReceiveWindowTargetBytes, _maxConnectionBufferedBytes));
            _expectContinueTimeoutMs = streamingOptions.ExpectContinueTimeoutMs;
            _stallTimeoutMs = options.TestStallTimeoutMillisecondsOverride > 0
                ? options.TestStallTimeoutMillisecondsOverride
                : (long)TimeSpan.FromSeconds(streamingOptions.Http2StallTimeoutSeconds).TotalMilliseconds;
            _maintenanceInterval = TimeSpan.FromMilliseconds(
                options.TestMaintenanceIntervalMillisecondsOverride > 0
                    ? options.TestMaintenanceIntervalMillisecondsOverride
                    : (int)DefaultMaintenanceInterval.TotalMilliseconds);
            _settingsAckSource = new ResettableValueTaskSource<bool>();
            _settingsAckSource.PrepareForUse();
        }

        // Backward-compatible constructor that uses default HTTP/2 and streaming options.
        public Http2Connection(Stream stream, string host, int port)
            : this(stream, host, port, new Http2Options(), new StreamingOptions())
        {
        }

        // Backward-compatible constructor that uses default streaming options.
        public Http2Connection(Stream stream, string host, int port, Http2Options options)
            : this(stream, host, port, options, new StreamingOptions())
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

            if (_maintenanceTask == null)
                _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Dispatch an HTTP/2 request and wait for the handler-driven response lifecycle to complete.
        /// Callers are responsible for invoking <see cref="IHttpHandler.OnRequestStart"/> before
        /// dispatching when they bypass <see cref="RawSocketTransport"/>.
        /// </summary>
        public async Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken ct)
        {
            context.SetState(TransportBehaviorFlags.SelfDrainsResponseBody, true);

            Http2Stream stream = null;
            int streamId = -1;
            bool requestHeadersSent = false;
            bool requestEndStreamSent = false;

            try
            {
                bool shouldWaitForExpectContinue = ExpectContinueHelper.ShouldAwaitExpectContinue(request);
                bool hasBody = shouldWaitForExpectContinue || ExpectContinueHelper.HasRequestBody(request.Content);
                bool hasRequestTrailers = request.Content != null && request.Content.TrailerProvider != null;

                // The request token only gates acquiring the write lock. Stream-id allocation and the
                // HEADERS write share one _writeLock hold so ids reach the wire in increasing order
                // (RFC 9113 Section 5.1.1), and a request cancelled while waiting consumes no id.
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    lock (_streamCreateLock)
                    {
                        if (_goawayReceived)
                        {
                            throw new UHttpException(new UHttpError(
                                UHttpErrorType.NetworkError,
                                "Connection received GOAWAY"));
                        }
                        if (_cts.IsCancellationRequested)
                        {
                            throw new UHttpException(new UHttpError(
                                UHttpErrorType.NetworkError,
                                "Connection is closed"));
                        }

                        if (_activeStreams.Count >= _remoteSettings.MaxConcurrentStreams)
                        {
                            throw new UHttpException(new UHttpError(
                                UHttpErrorType.NetworkError,
                                $"MAX_CONCURRENT_STREAMS limit reached ({_remoteSettings.MaxConcurrentStreams})"));
                        }

                        streamId = AllocateNextStreamId();
                        if (_goawayReceived || (_lastGoawayStreamId > 0 && streamId > _lastGoawayStreamId))
                        {
                            throw new UHttpException(new UHttpError(
                                UHttpErrorType.NetworkError,
                                "Connection closed during stream creation"));
                        }

                        // Registered before HEADERS hit the wire: the response may arrive at once.
                        stream = Http2StreamPool.Rent(
                            streamId,
                            request,
                            handler,
                            context,
                            _remoteSettings.InitialWindowSize,
                            _localSettings.InitialWindowSize,
                            this);
                        _activeStreams[streamId] = stream;
                    }

                    if (shouldWaitForExpectContinue)
                        stream.EnableExpectContinueWait();

                    _headerListScratch.Clear();
                    _headerListScratch.Add((":method", request.Method.ToUpperString()));
                    _headerListScratch.Add((":scheme", _schemeHeader));
                    _headerListScratch.Add((":authority", BuildAuthorityValue(request.Uri)));
                    _headerListScratch.Add((":path", request.Uri.PathAndQuery ?? "/"));

                    foreach (var name in request.Headers.Names)
                    {
                        if (IsHttp2ForbiddenHeader(name) ||
                            (hasRequestTrailers &&
                             string.Equals(name, "trailer", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

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

                    if (hasRequestTrailers)
                        AppendRequestTrailerDeclarationHeader(_headerListScratch, request.Content.DeclaredTrailerNames);

                    if (!request.Headers.Contains("user-agent"))
                        _headerListScratch.Add(("user-agent", "TurboHTTP/1.0"));

                    var headerBlock = _hpackEncoder.Encode(_headerListScratch);

                    // Set before the write: no response frame can be processed before HEADERS are out.
                    stream.State = Http2StreamState.Open;
                    await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody)
                        .ConfigureAwait(false);
                    requestHeadersSent = true;
                    requestEndStreamSent = !hasBody;
                    if (!hasBody)
                        OnRequestEndStreamSent(stream);
                }
                finally
                {
                    _writeLock.Release();
                }

                // Registered only once HEADERS are on the wire, so the cancellation RST_STREAM can
                // never precede them (an RST_STREAM on an idle stream is a connection error,
                // RFC 9113 Section 6.4). An already-cancelled token runs the callback here.
                stream.SetRequestCancellationToken(ct);
                stream.SetCancellationRegistration(ct.Register(
                    s_requestCancellationCallback,
                    stream,
                    useSynchronizationContext: false));

                if (hasBody)
                {
                    bool sendRequestBody = true;
                    if (shouldWaitForExpectContinue)
                        sendRequestBody = await WaitForExpectContinueAsync(stream, ct).ConfigureAwait(false);

                    if (sendRequestBody)
                    {
                        // The upload continues after the response starts or completes, until the body
                        // ends or the stream is reset or abandoned (RFC 9113 Section 8.1).
                        var requestBodyBytesSent = await SendDataAsync(streamId, request.Content, stream, ct)
                            .ConfigureAwait(false);
                        context.SetState(TransportBehaviorFlags.RequestBodyBytesSent, requestBodyBytesSent);
                        requestEndStreamSent = stream.IsLocalClosed;
                        if (!requestEndStreamSent)
                        {
                            // Stopped without END_STREAM: the stream must still end on the wire.
                            requestEndStreamSent = true;
                            await ResetStreamIfActiveAsync(streamId, stream, Http2ErrorCode.Cancel)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // Early final response to Expect: 100-continue (RFC 9110 Section 10.1.1): the body
                        // is never sent, and the stream ends with RST_STREAM(NO_ERROR) once the response
                        // is complete (RFC 9113 Section 8.1), so the server keeps no half-open stream.
                        context.SetState(TransportBehaviorFlags.RequestBodyBytesSent, 0L);
                        requestEndStreamSent = true;
                        if (stream.MarkLocalAbandoned())
                        {
                            await ResetStreamIfActiveAsync(streamId, stream, Http2ErrorCode.NoError)
                                .ConfigureAwait(false);
                        }
                    }

                    stream.ReleaseCancellationIfStreamingHandedOff();
                }

                context.RecordEvent("TransportH2RequestSent");
                await AwaitResponseCompletionOrCancellationAsync(stream)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || !_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes an OperationCanceledException raised by the connection token (the
                // connection was failed or disposed): mapped to a network error below.
                if (stream != null &&
                    requestHeadersSent &&
                    !requestEndStreamSent &&
                    !ct.IsCancellationRequested)
                {
                    stream.AbortRequestBody();
                    try
                    {
                        await ResetStreamIfActiveAsync(streamId, stream, Http2ErrorCode.Cancel)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best effort only: the underlying connection may already be failing.
                    }
                }

                if (ex is HandlerCallbackException handlerFault)
                {
                    context.RecordEvent("RequestFailed");
                    ExceptionDispatchInfo.Capture(handlerFault.InnerException ?? handlerFault).Throw();
                    throw;
                }

                throw MapException(ex);
            }
            finally
            {
                if (stream != null)
                {
                    if (!stream.ResponseStarted)
                        RemoveActiveStream(streamId, stream);

                    stream.ReleaseCancellationRegistration();
                    stream.ReleaseDispatchLifetime();
                }
            }
        }

        private static UHttpException MapException(Exception ex) =>
            ex as UHttpException ??
            new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));

        private async Task AwaitResponseCompletionOrCancellationAsync(Http2Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            // Request-scoped cancellation is owned by the stream's cancellation registration, which
            // faults the stream and emits the best-effort RST_STREAM when the stream is still active.
            await stream.CompletionTask.ConfigureAwait(false);
        }

        private async Task<bool> WaitForExpectContinueAsync(Http2Stream stream, CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var expectContinueTask = stream.WaitForExpectContinueAsync();
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(_expectContinueTimeoutMs, delayCts.Token);

            var winner = await Task.WhenAny(expectContinueTask, timeoutTask).ConfigureAwait(false);
            if (winner == expectContinueTask)
            {
                delayCts.Cancel();
                return await expectContinueTask.ConfigureAwait(false);
            }

            // Re-check the signal task before proceeding so a near-simultaneous
            // 100/final HEADERS completion still wins over the timeout branch.
            if (expectContinueTask.IsCompleted)
            {
                delayCts.Cancel();
                return await expectContinueTask.ConfigureAwait(false);
            }

            stream.SignalExpectContinueContinue();
            return true;
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

        private static void AppendRequestTrailerDeclarationHeader(
            List<(string Name, string Value)> headers,
            IReadOnlyList<string> declaredTrailerNames)
        {
            if (headers == null || declaredTrailerNames == null || declaredTrailerNames.Count == 0)
                return;

            headers.Add(("trailer", BuildDeclaredTrailerHeaderValue(declaredTrailerNames)));
        }

        private static string BuildDeclaredTrailerHeaderValue(IReadOnlyList<string> declaredTrailerNames)
        {
            if (declaredTrailerNames == null || declaredTrailerNames.Count == 0)
                return string.Empty;

            if (declaredTrailerNames.Count == 1)
                return declaredTrailerNames[0];

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < declaredTrailerNames.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(declaredTrailerNames[i]);
            }

            return builder.ToString();
        }
    }
}
