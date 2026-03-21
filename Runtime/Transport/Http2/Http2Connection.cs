using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

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
        private readonly SemaphoreSlim _windowWaiter = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _connectionRecvWindowUpdateLock = new SemaphoreSlim(1, 1);
        private long _connectionBufferedBytes;

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
        private static readonly TimeSpan KeepAlivePingInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SettingsAckTimeout = TimeSpan.FromSeconds(5);

        // CONTINUATION tracking
        private int _continuationStreamId;

        // Initialization handshake
        private readonly ResettableValueTaskSource<bool> _settingsAckSource;

        // HPACK table size tracking (for detecting changes including back-to-default)
        private int _lastHeaderTableSize = Http2Constants.DefaultHeaderTableSize;
        private readonly int _perStreamReceiveBufferBytes;
        private readonly int _streamingSendBufferBytes;
        private readonly int _maxConnectionBufferedBytes;
        private readonly int _connectionRecvWindowTarget;
        private readonly TimeSpan _maintenanceInterval;
        private long _stallTimeoutMs;
        private readonly ConcurrentDictionary<int, long> _recentlyResetStreams =
            new ConcurrentDictionary<int, long>();

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

            if (ct.IsCancellationRequested)
            {
                RemoveActiveStream(streamId);
                throw new OperationCanceledException(ct);
            }

            stream.CancellationRegistration = ct.Register(() =>
            {
                if (_activeStreams.TryRemove(streamId, out var canceledStream))
                {
                    TrackRecentlyResetStream(streamId);
                    _ = SendRstStreamAsync(streamId, Http2ErrorCode.Cancel);
                    canceledStream.Cancel(ct);
                }
            });

            try
            {
                bool hasBody = HasRequestBody(request.Content);

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
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

                    var headerBlock = _hpackEncoder.Encode(_headerListScratch);

                    await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct)
                        .ConfigureAwait(false);
                    requestHeadersSent = true;
                    requestEndStreamSent = !hasBody;

                    stream.State = hasBody ? Http2StreamState.Open : Http2StreamState.HalfClosedLocal;
                }
                finally
                {
                    _writeLock.Release();
                }

                if (hasBody)
                {
                    var requestBodyBytesSent = await SendDataAsync(streamId, request.Content, stream, ct)
                        .ConfigureAwait(false);
                    context.SetState(TransportBehaviorFlags.RequestBodyBytesSent, requestBodyBytesSent);
                    requestEndStreamSent = true;
                    stream.State = Http2StreamState.HalfClosedLocal;
                }

                context.RecordEvent("TransportH2RequestSent");
                await AwaitResponseCompletionOrCancellationAsync(stream, streamId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (stream != null &&
                    requestHeadersSent &&
                    !requestEndStreamSent &&
                    !ct.IsCancellationRequested)
                {
                    RemoveActiveStream(streamId);
                    try
                    {
                        TrackRecentlyResetStream(streamId);
                        await SendRstStreamAsync(streamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
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
                        RemoveActiveStream(streamId);

                    stream.CancellationRegistration.Dispose();
                    stream.CancellationRegistration = default;
                    stream.ReleaseDispatchLifetime();
                }
            }
        }

        private static UHttpException MapException(Exception ex) =>
            ex as UHttpException ??
            new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));

        private async Task AwaitResponseCompletionOrCancellationAsync(
            Http2Stream stream,
            int streamId,
            CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!ct.CanBeCanceled)
            {
                await stream.CompletionTask.ConfigureAwait(false);
                return;
            }

            var completionTask = stream.CompletionTask.AsTask();
            var cancellationTask = Task.Delay(Timeout.Infinite, ct);
            var completedTask = await Task.WhenAny(completionTask, cancellationTask).ConfigureAwait(false);
            if (completedTask == completionTask)
            {
                await completionTask.ConfigureAwait(false);
                return;
            }

            if (_activeStreams.TryRemove(streamId, out var canceledStream))
            {
                TrackRecentlyResetStream(streamId);
                canceledStream.Cancel(ct);

                try
                {
                    await SendRstStreamAsync(streamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested || ct.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    FailAllStreams(ex);
                }
            }

            throw new OperationCanceledException(ct);
        }

        private static bool HasRequestBody(UHttpRequestBody content)
        {
            if (content == null)
                return false;

            if (content.TryGetBufferedData(out var buffered))
                return !buffered.IsEmpty;

            return !content.Length.HasValue || content.Length.Value > 0;
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
