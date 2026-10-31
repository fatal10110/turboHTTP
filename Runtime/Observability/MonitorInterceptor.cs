using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Interceptor that captures request/response snapshots for the Editor monitor.
    /// </summary>
    /// <remarks>
    /// Capture is off unless <see cref="CaptureEnabled"/> is set and either a viewer is attached
    /// (<see cref="AttachViewer"/>, done by the HTTP Monitor window while it is open) or
    /// <see cref="CaptureWithoutViewer"/> is set. Captured data is redacted at capture time with
    /// <see cref="RedactionPolicy"/> and history is bounded by <see cref="HistoryCapacity"/> entries
    /// and <see cref="MaxTotalCaptureBytes"/> bytes.
    /// </remarks>
    public sealed class MonitorInterceptor : IHttpInterceptor
    {
        private const int DefaultHistoryCapacity = 1000;
        private const int DefaultMaxCaptureSizeBytes = 5 * 1024 * 1024; // 5 MB
        private const int DefaultBinaryPreviewBytes = 64 * 1024; // 64 KB
        private const long DefaultMaxTotalCaptureBytes = 32L * 1024 * 1024; // 32 MB
        private const int BinaryDetectionSampleBytes = 512;
        private static readonly TimeSpan CaptureErrorLogCooldown = TimeSpan.FromSeconds(30);

        private static readonly object HistoryLock = new object();
        private static readonly object CaptureErrorLogLock = new object();
        private static HttpMonitorEvent[] _historyBuffer = new HttpMonitorEvent[DefaultHistoryCapacity];
        private static long[] _historySizes = new long[DefaultHistoryCapacity];
        private static long _historyBytes;
        private static long _maxTotalCaptureBytes = DefaultMaxTotalCaptureBytes;
        private static int _viewerCount;
        private static int _captureWithoutViewer;
        private static RedactionPolicy _redactionPolicy = RedactionPolicy.Default;
        private static int _historyStart;
        private static int _historyCount;
        private static int _historyCapacity = DefaultHistoryCapacity;
        private static int _maxCaptureSizeBytes = DefaultMaxCaptureSizeBytes;
        private static int _binaryPreviewBytes = DefaultBinaryPreviewBytes;
        private static int _captureEnabled = 1;
        private static long _nextCaptureErrorLogUtcTicks = DateTime.MinValue.Ticks;
        private static int _suppressedCaptureErrorCount;

        private readonly struct BodySnapshot
        {
            public static readonly BodySnapshot Empty =
                new BodySnapshot(ReadOnlyMemory<byte>.Empty, 0, false, false);

            public readonly ReadOnlyMemory<byte> Body;
            public readonly int OriginalSize;
            public readonly bool IsTruncated;
            public readonly bool IsBinary;

            public BodySnapshot(
                ReadOnlyMemory<byte> body,
                int originalSize,
                bool isTruncated,
                bool isBinary)
            {
                Body = body;
                OriginalSize = originalSize;
                IsTruncated = isTruncated;
                IsBinary = isBinary;
            }
        }

        /// <summary>
        /// Raised when history changes. Null payload means structural update (for example clear).
        /// Thread safety: event invocation is thread-safe (per-handler isolation).
        /// Subscription/unsubscription should occur on the main thread.
        /// </summary>
        public static event Action<HttpMonitorEvent> OnRequestCaptured;

        private static Func<string, string, string> _headerValueTransform;

        /// <summary>
        /// Optional hook for value-level header transformations (for example redaction).
        /// Disabled by default. Thread-safe: subscription/unsubscription may occur on any thread.
        /// </summary>
        public static Func<string, string, string> HeaderValueTransform
        {
            get => Volatile.Read(ref _headerValueTransform);
            set => Volatile.Write(ref _headerValueTransform, value);
        }

        private static Action<string> _diagnosticLogger;

        /// <summary>
        /// Optional diagnostics sink used for throttled capture-failure logs.
        /// </summary>
        public static Action<string> DiagnosticLogger
        {
            get => Volatile.Read(ref _diagnosticLogger);
            set => Volatile.Write(ref _diagnosticLogger, value);
        }

        /// <summary>
        /// Master switch (the "Enable HTTP Monitor" preference). Capture also needs an attached viewer
        /// or <see cref="CaptureWithoutViewer"/>; see <see cref="IsCapturing"/>.
        /// </summary>
        public static bool CaptureEnabled
        {
            get => Volatile.Read(ref _captureEnabled) == 1;
            set => Volatile.Write(ref _captureEnabled, value ? 1 : 0);
        }

        /// <summary>
        /// Opt-in: capture while no viewer is attached (no HTTP Monitor window open). Default <c>false</c>.
        /// </summary>
        public static bool CaptureWithoutViewer
        {
            get => Volatile.Read(ref _captureWithoutViewer) == 1;
            set => Volatile.Write(ref _captureWithoutViewer, value ? 1 : 0);
        }

        /// <summary>Number of attached viewers (open HTTP Monitor windows).</summary>
        public static int ViewerCount => Volatile.Read(ref _viewerCount);

        /// <summary>
        /// Whether requests are captured now: <see cref="CaptureEnabled"/> and (a viewer is attached
        /// or <see cref="CaptureWithoutViewer"/>). When <c>false</c> the interceptor adds no per-request work.
        /// </summary>
        public static bool IsCapturing =>
            CaptureEnabled && (Volatile.Read(ref _viewerCount) > 0 || CaptureWithoutViewer);

        /// <summary>
        /// Registers a viewer (for example the HTTP Monitor window in <c>OnEnable</c>). Capture runs while
        /// at least one viewer is attached. Dispose the returned handle to detach; disposing twice is a no-op.
        /// </summary>
        public static IDisposable AttachViewer()
        {
            Interlocked.Increment(ref _viewerCount);
            return new ViewerHandle();
        }

        /// <summary>
        /// Redaction applied at capture time to URLs (query values, user-info) and header values.
        /// Default <see cref="Observability.RedactionPolicy.Default"/>; setting <c>null</c> restores it.
        /// Opt out explicitly with <see cref="Observability.RedactionPolicy.None"/>.
        /// </summary>
        public static RedactionPolicy RedactionPolicy
        {
            get => Volatile.Read(ref _redactionPolicy);
            set => Volatile.Write(ref _redactionPolicy, value ?? RedactionPolicy.Default);
        }

        /// <summary>
        /// Budget for the bytes held by the capture history (bodies plus an estimate of URL and header
        /// strings). Oldest entries are evicted to stay within it, each captured body is truncated to a
        /// quarter of it, and an event that still exceeds it (huge headers) is not stored. Minimum 1 KB; default 32 MB.
        /// </summary>
        public static long MaxTotalCaptureBytes
        {
            get => Interlocked.Read(ref _maxTotalCaptureBytes);
            set
            {
                // Interlocked: a 64-bit field must not tear on 32-bit IL2CPP targets.
                Interlocked.Exchange(ref _maxTotalCaptureBytes, Math.Max(1024L, value));
                lock (HistoryLock)
                {
                    EvictToBudgetLocked(0);
                }
            }
        }

        /// <summary>Bytes currently held by the capture history (see <see cref="MaxTotalCaptureBytes"/>).</summary>
        public static long TotalCapturedBytes
        {
            get
            {
                lock (HistoryLock)
                {
                    return _historyBytes;
                }
            }
        }

        public static int HistoryCount
        {
            get
            {
                lock (HistoryLock)
                {
                    return _historyCount;
                }
            }
        }

        public static int HistoryCapacity
        {
            get
            {
                lock (HistoryLock)
                {
                    return _historyCapacity;
                }
            }
            set
            {
                var clamped = Math.Max(1, value);
                lock (HistoryLock)
                {
                    if (_historyCapacity == clamped)
                    {
                        return;
                    }

                    _historyCapacity = clamped;
                    ResizeBufferLocked(clamped);
                }
            }
        }

        public static int MaxCaptureSizeBytes
        {
            get => Volatile.Read(ref _maxCaptureSizeBytes);
            set => Volatile.Write(ref _maxCaptureSizeBytes, Math.Max(0, value));
        }

        public static int BinaryPreviewBytes
        {
            get => Volatile.Read(ref _binaryPreviewBytes);
            set => Volatile.Write(ref _binaryPreviewBytes, Math.Max(0, value));
        }

        public static void GetHistorySnapshot(List<HttpMonitorEvent> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            lock (HistoryLock)
            {
                buffer.Clear();
                for (int i = 0; i < _historyCount; i++)
                {
                    var index = (_historyStart + i) % _historyBuffer.Length;
                    var evt = _historyBuffer[index];
                    if (evt != null)
                    {
                        buffer.Add(evt);
                    }
                }
            }
        }

        public static void ClearHistory()
        {
            lock (HistoryLock)
            {
                Array.Clear(_historyBuffer, 0, _historyBuffer.Length);
                Array.Clear(_historySizes, 0, _historySizes.Length);
                _historyStart = 0;
                _historyCount = 0;
                _historyBytes = 0;
            }

            ResetCaptureFailureThrottle();

            PublishEvent(null);
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (!IsCapturing)
                    return next(request, handler, context, cancellationToken);

                return next(
                    request,
                    new MonitorHandler(handler, request),
                    context,
                    cancellationToken);
            };
        }

        internal static void Capture(
            UHttpRequest request,
            int statusCode,
            HttpHeaders responseHeaders,
            ReadOnlySequence<byte> responseBody,
            RequestContext context,
            Exception exception)
        {
            var originalResponseBodySize = responseBody.Length > int.MaxValue
                ? int.MaxValue
                : (int)responseBody.Length;
            Capture(
                request,
                statusCode,
                responseHeaders,
                responseBody,
                originalResponseBodySize,
                responseBodyWasTruncated: false,
                context,
                exception);
        }

        internal static void Capture(
            UHttpRequest request,
            int statusCode,
            HttpHeaders responseHeaders,
            ReadOnlySequence<byte> responseBody,
            int originalResponseBodySize,
            bool responseBodyWasTruncated,
            RequestContext context,
            Exception exception)
        {
            try
            {
                var monitorEvent = BuildMonitorEvent(
                    request,
                    statusCode,
                    responseHeaders,
                    responseBody,
                    originalResponseBodySize,
                    responseBodyWasTruncated,
                    context,
                    exception);
                if (StoreEvent(monitorEvent))
                    PublishEvent(monitorEvent);
            }
            catch (Exception captureEx)
            {
                LogCaptureFailure(captureEx);
            }
        }

        private static HttpMonitorEvent BuildMonitorEvent(
            UHttpRequest request,
            int statusCode,
            HttpHeaders responseHeaders,
            ReadOnlySequence<byte> responseBody,
            int originalResponseBodySize,
            bool responseBodyWasTruncated,
            RequestContext context,
            Exception exception)
        {
            var redactionPolicy = RedactionPolicy;
            var requestHeaders = CopyHeaders(request?.Headers, redactionPolicy);
            ReadOnlyMemory<byte> requestBody = ReadOnlyMemory<byte>.Empty;
            var requestBodyCaptured = request != null && request.TryGetBufferedContent(out requestBody);
            var requestSnapshot = CreateBodySnapshot(
                requestBodyCaptured
                    ? requestBody
                    : ReadOnlyMemory<byte>.Empty,
                requestHeaders);
            var requestBodyCaptureNote = requestBodyCaptured
                ? string.Empty
                : BuildRequestBodyCaptureNote(request?.Content);

            var copiedResponseHeaders = CopyHeaders(responseHeaders, redactionPolicy);
            var responseSnapshot = CreateBodySnapshot(
                responseBody,
                copiedResponseHeaders,
                originalResponseBodySize,
                responseBodyWasTruncated);

            var (error, errorType, failureKind) = ResolveError(statusCode, exception);
            var statusText = statusCode != 0 ? ((HttpStatusCode)statusCode).ToString() : string.Empty;
            var elapsed = context != null ? context.Elapsed : TimeSpan.Zero;
            var timeline = CopyTimeline(context?.Timeline);

            return new HttpMonitorEvent(
                id: Guid.NewGuid().ToString("N"),
                timestamp: DateTime.UtcNow,
                method: request?.Method.ToString(),
                url: redactionPolicy.RedactUrl(request?.Uri),
                requestHeaders: requestHeaders,
                requestBody: requestSnapshot.Body,
                requestBodyCaptureNote: requestBodyCaptureNote,
                originalRequestBodySize: requestSnapshot.OriginalSize,
                isRequestBodyTruncated: requestSnapshot.IsTruncated,
                isRequestBodyBinary: requestSnapshot.IsBinary,
                statusCode: statusCode,
                statusText: statusText,
                responseHeaders: copiedResponseHeaders,
                responseBody: responseSnapshot.Body,
                originalResponseBodySize: responseSnapshot.OriginalSize,
                isResponseBodyTruncated: responseSnapshot.IsTruncated,
                isResponseBodyBinary: responseSnapshot.IsBinary,
                elapsedTime: elapsed,
                timeline: timeline,
                error: error,
                errorType: errorType,
                failureKind: failureKind);
        }

        private static string BuildRequestBodyCaptureNote(UHttpRequestBody content)
        {
            if (content == null || content.IsEmpty)
                return string.Empty;

            var length = content.Length.HasValue
                ? content.Length.Value.ToString()
                : "unknown";

            return $"<Streaming request body unavailable without buffering; length={length}; replayability={content.Replayability}>";
        }

        private static IReadOnlyDictionary<string, string> CopyHeaders(
            HttpHeaders headers,
            RedactionPolicy redactionPolicy)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return copy;
            }

            // Use Names + GetValues to capture all multi-value headers.
            // The IEnumerable<KeyValuePair> enumerator only yields the first value per name.
            var transform = Volatile.Read(ref _headerValueTransform);
            foreach (var name in headers.Names)
            {
                var headerKey = name ?? string.Empty;
                var values = headers.GetValues(name);
                if (values.Count == 0)
                {
                    continue;
                }

                string value;
                if (values.Count == 1)
                {
                    value = values[0] ?? string.Empty;
                }
                else
                {
                    // Set-Cookie must not be comma-folded (RFC 6265).
                    var separator = string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
                        ? "; " : ", ";
                    value = string.Join(separator, values);
                }

                // Policy first: a throwing transform below then leaves the redacted value in place.
                value = redactionPolicy.RedactHeaderValue(headerKey, value);

                if (transform != null)
                {
                    try
                    {
                        value = transform(headerKey, value) ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        LogCaptureFailure(ex);
                    }
                }

                copy[headerKey] = value;
            }

            return copy;
        }

        private static BodySnapshot CreateBodySnapshot(
            byte[] body,
            IReadOnlyDictionary<string, string> headers)
        {
            if (body == null || body.Length == 0)
            {
                return BodySnapshot.Empty;
            }

            return CreateBodySnapshot(new ReadOnlyMemory<byte>(body), headers);
        }

        private static BodySnapshot CreateBodySnapshot(
            ReadOnlyMemory<byte> body,
            IReadOnlyDictionary<string, string> headers)
        {
            return CreateBodySnapshot(
                body,
                headers,
                body.Length,
                wasTruncatedBeforeSnapshot: false);
        }

        private static BodySnapshot CreateBodySnapshot(
            ReadOnlyMemory<byte> body,
            IReadOnlyDictionary<string, string> headers,
            int originalSizeHint,
            bool wasTruncatedBeforeSnapshot)
        {
            if (body.IsEmpty)
            {
                return BodySnapshot.Empty;
            }

            var originalSize = Math.Max(originalSizeHint, body.Length);
            var isBinary = HttpMonitorEvent.IsLikelyBinaryPayload(body, headers);
            var limit = GetBodyCaptureLimit(isBinary);
            var captureSize = Math.Min(body.Length, Math.Max(0, limit));
            var isTruncated = wasTruncatedBeforeSnapshot || captureSize < originalSize;

            if (captureSize == 0)
            {
                return new BodySnapshot(ReadOnlyMemory<byte>.Empty, originalSize, true, isBinary);
            }

            var snapshot = new byte[captureSize];
            body.Slice(0, captureSize).CopyTo(snapshot.AsMemory());
            return new BodySnapshot(snapshot, originalSize, isTruncated, isBinary);
        }

        private static BodySnapshot CreateBodySnapshot(
            ReadOnlySequence<byte> body,
            IReadOnlyDictionary<string, string> headers)
        {
            var originalSize = body.Length > int.MaxValue
                ? int.MaxValue
                : (int)body.Length;
            return CreateBodySnapshot(body, headers, originalSize, wasTruncatedBeforeSnapshot: false);
        }

        private static BodySnapshot CreateBodySnapshot(
            ReadOnlySequence<byte> body,
            IReadOnlyDictionary<string, string> headers,
            int originalSizeHint,
            bool wasTruncatedBeforeSnapshot)
        {
            if (body.IsEmpty)
            {
                return BodySnapshot.Empty;
            }

            if (body.IsSingleSegment)
            {
                return CreateBodySnapshot(
                    body.First,
                    headers,
                    originalSizeHint,
                    wasTruncatedBeforeSnapshot);
            }

            var originalSize = Math.Max(
                originalSizeHint,
                body.Length > int.MaxValue ? int.MaxValue : (int)body.Length);

            var sampleLength = (int)Math.Min(body.Length, BinaryDetectionSampleBytes);
            var sample = sampleLength > 0
                ? CopyPrefix(body, sampleLength)
                : Array.Empty<byte>();
            var isBinary = HttpMonitorEvent.IsLikelyBinaryPayload(sample, headers);
            var limit = GetBodyCaptureLimit(isBinary);
            var captureSize = (int)Math.Min(body.Length, Math.Max(0L, limit));
            var isTruncated = wasTruncatedBeforeSnapshot || captureSize < originalSize;

            if (captureSize == 0)
            {
                return new BodySnapshot(ReadOnlyMemory<byte>.Empty, originalSize, true, isBinary);
            }

            if (sample.Length == captureSize)
            {
                return new BodySnapshot(sample, originalSize, isTruncated, isBinary);
            }

            byte[] snapshot;
            if (sample.Length > captureSize)
            {
                snapshot = new byte[captureSize];
                sample.AsSpan(0, captureSize).CopyTo(snapshot);
            }
            else
            {
                snapshot = CopyPrefix(body, captureSize);
            }

            return new BodySnapshot(snapshot, originalSize, isTruncated, isBinary);
        }

        internal static int GetBufferedResponseCaptureLimit(HttpHeaders headers = null)
        {
            var liveCaptureLimit = HasClearlyBinaryContentType(headers)
                ? BinaryPreviewBytes
                : Math.Max(MaxCaptureSizeBytes, BinaryPreviewBytes);
            liveCaptureLimit = Math.Min(liveCaptureLimit, GetBudgetBodyLimit());
            return Math.Max(liveCaptureLimit, BinaryDetectionSampleBytes);
        }

        private static int GetBodyCaptureLimit(bool isBinary)
        {
            var limit = isBinary ? BinaryPreviewBytes : MaxCaptureSizeBytes;
            return Math.Min(limit, GetBudgetBodyLimit());
        }

        // Each body may use at most a quarter of the total budget, so one event cannot take the whole
        // history and its two bodies plus URL/headers normally fit the budget.
        private static int GetBudgetBodyLimit()
        {
            var quarter = MaxTotalCaptureBytes / 4;
            return quarter > int.MaxValue ? int.MaxValue : (int)quarter;
        }

        private static bool HasClearlyBinaryContentType(HttpHeaders headers)
        {
            var contentType = headers?.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType))
                return false;

            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("javascript", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("graphql", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
                || contentType.IndexOf("application/octet-stream", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("application/x-protobuf", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("application/vnd.unity", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("application/pdf", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("application/zip", StringComparison.OrdinalIgnoreCase) >= 0
                || contentType.IndexOf("application/gzip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyList<HttpMonitorTimelineEvent> CopyTimeline(
            IReadOnlyList<TimelineEvent> timeline)
        {
            if (timeline == null || timeline.Count == 0)
            {
                return Array.Empty<HttpMonitorTimelineEvent>();
            }

            var copy = new List<HttpMonitorTimelineEvent>(timeline.Count);
            for (int i = 0; i < timeline.Count; i++)
            {
                var evt = timeline[i];
                if (evt == null)
                {
                    continue;
                }

                copy.Add(new HttpMonitorTimelineEvent(
                    evt.Name,
                    evt.Timestamp,
                    CopyTimelineData(evt.Data)));
            }

            return copy;
        }

        private static IReadOnlyDictionary<string, string> CopyTimelineData(
            IReadOnlyDictionary<string, object> data)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var copy = new Dictionary<string, string>(data.Count, StringComparer.Ordinal);
            foreach (var pair in data)
            {
                copy[pair.Key ?? string.Empty] = ConvertTimelineValue(pair.Value);
            }

            return copy;
        }

        private static string ConvertTimelineValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            try
            {
                return value.ToString() ?? string.Empty;
            }
            catch
            {
                return "<non-stringable>";
            }
        }

        private static (string Error, UHttpErrorType? ErrorType, HttpMonitorFailureKind FailureKind) ResolveError(
            int statusCode,
            Exception exception)
        {
            if (exception is UHttpException httpException && httpException.HttpError != null)
            {
                var type = httpException.HttpError.Type;
                var failureKind = type == UHttpErrorType.HttpError
                    ? HttpMonitorFailureKind.HttpStatusError
                    : HttpMonitorFailureKind.TransportError;
                return (httpException.HttpError.ToString(), type, failureKind);
            }

            if (exception != null)
            {
                return (exception.Message ?? "Transport failure", null, HttpMonitorFailureKind.TransportError);
            }

            if (statusCode >= 400)
            {
                return (
                    $"HTTP {statusCode} {((HttpStatusCode)statusCode)}",
                    UHttpErrorType.HttpError,
                    HttpMonitorFailureKind.HttpStatusError);
            }

            return (string.Empty, null, HttpMonitorFailureKind.None);
        }

        private static byte[] CopyPrefix(ReadOnlySequence<byte> body, int length)
        {
            if (length <= 0)
                return Array.Empty<byte>();

            var snapshot = new byte[length];
            var copied = 0;
            foreach (var segment in body)
            {
                var span = segment.Span;
                if (span.IsEmpty)
                    continue;

                var toCopy = Math.Min(span.Length, length - copied);
                span.Slice(0, toCopy).CopyTo(snapshot.AsSpan(copied));
                copied += toCopy;
                if (copied >= length)
                    break;
            }

            return snapshot;
        }

        private static bool StoreEvent(HttpMonitorEvent monitorEvent)
        {
            var size = EstimateEventSize(monitorEvent);
            lock (HistoryLock)
            {
                if (size > Interlocked.Read(ref _maxTotalCaptureBytes))
                    return false;

                if (_historyBuffer.Length != _historyCapacity)
                {
                    ResizeBufferLocked(_historyCapacity);
                }

                EvictToBudgetLocked(size);

                if (_historyCount == _historyBuffer.Length)
                {
                    RemoveOldestLocked();
                }

                var insertIndex = (_historyStart + _historyCount) % _historyBuffer.Length;
                _historyBuffer[insertIndex] = monitorEvent;
                _historySizes[insertIndex] = size;
                _historyCount++;
                _historyBytes += size;
                return true;
            }
        }

        private static void EvictToBudgetLocked(long incomingSize)
        {
            var budget = Interlocked.Read(ref _maxTotalCaptureBytes);
            while (_historyCount > 0 && _historyBytes + incomingSize > budget)
            {
                RemoveOldestLocked();
            }
        }

        private static void RemoveOldestLocked()
        {
            _historyBytes -= _historySizes[_historyStart];
            _historyBuffer[_historyStart] = null;
            _historySizes[_historyStart] = 0;
            _historyStart = (_historyStart + 1) % _historyBuffer.Length;
            _historyCount--;
        }

        internal static long EstimateEventSize(HttpMonitorEvent monitorEvent)
        {
            if (monitorEvent == null)
                return 0;

            // Bodies are byte arrays; strings are UTF-16 (2 bytes per char) plus a fixed object overhead.
            const long ObjectOverhead = 256;
            long size = ObjectOverhead + monitorEvent.RequestBody.Length + monitorEvent.ResponseBody.Length;
            size += 2L * ((monitorEvent.Url?.Length ?? 0) + (monitorEvent.Error?.Length ?? 0)
                + (monitorEvent.RequestBodyCaptureNote?.Length ?? 0));
            size += EstimateHeadersSize(monitorEvent.RequestHeaders);
            size += EstimateHeadersSize(monitorEvent.ResponseHeaders);
            return size;
        }

        private static long EstimateHeadersSize(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null)
                return 0;

            long size = 0;
            foreach (var pair in headers)
            {
                size += 2L * ((pair.Key?.Length ?? 0) + (pair.Value?.Length ?? 0)) + 32;
            }

            return size;
        }

        private static void ResizeBufferLocked(int newCapacity)
        {
            var capacity = Math.Max(1, newCapacity);
            var newBuffer = new HttpMonitorEvent[capacity];
            var newSizes = new long[capacity];

            var itemsToCopy = Math.Min(_historyCount, capacity);
            var skipCount = _historyCount - itemsToCopy;
            long bytes = 0;

            for (int i = 0; i < itemsToCopy; i++)
            {
                var sourceIndex = (_historyStart + skipCount + i) % _historyBuffer.Length;
                newBuffer[i] = _historyBuffer[sourceIndex];
                newSizes[i] = _historySizes[sourceIndex];
                bytes += newSizes[i];
            }

            _historyBuffer = newBuffer;
            _historySizes = newSizes;
            _historyStart = 0;
            _historyCount = itemsToCopy;
            _historyBytes = bytes;
        }

        private static void PublishEvent(HttpMonitorEvent monitorEvent)
        {
            var handlers = OnRequestCaptured;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<HttpMonitorEvent> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(monitorEvent);
                }
                catch (Exception ex)
                {
                    LogCaptureFailure(ex);
                }
            }
        }

        private static void LogCaptureFailure(Exception ex)
        {
            if (ex == null)
            {
                return;
            }

            bool shouldLog = false;
            int suppressedCount = 0;

            lock (CaptureErrorLogLock)
            {
                var now = DateTime.UtcNow;
                var nextCaptureErrorLogUtc = new DateTime(_nextCaptureErrorLogUtcTicks, DateTimeKind.Utc);
                if (now >= nextCaptureErrorLogUtc)
                {
                    shouldLog = true;
                    suppressedCount = _suppressedCaptureErrorCount;
                    _suppressedCaptureErrorCount = 0;
                    _nextCaptureErrorLogUtcTicks = (now + CaptureErrorLogCooldown).Ticks;
                }
                else
                {
                    _suppressedCaptureErrorCount++;
                }
            }

            if (!shouldLog)
            {
                return;
            }

            var message = $"[TurboHTTP][Monitor] Capture failure: {ex.Message}";
            if (suppressedCount > 0)
            {
                message += $" (suppressed {suppressedCount} similar failures)";
            }

            var logger = DiagnosticLogger;
            if (logger == null)
            {
                return;
            }

            try
            {
                logger(message);
            }
            catch
            {
                // Diagnostics path must never throw back into request processing.
            }
        }

        private static void ResetCaptureFailureThrottle()
        {
            lock (CaptureErrorLogLock)
            {
                _nextCaptureErrorLogUtcTicks = DateTime.MinValue.Ticks;
                _suppressedCaptureErrorCount = 0;
            }
        }

        private sealed class ViewerHandle : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref _viewerCount);
            }
        }
    }
}
