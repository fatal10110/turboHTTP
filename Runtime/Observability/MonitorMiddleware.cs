using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Middleware that captures request/response snapshots for the Editor monitor.
    /// </summary>
    public class MonitorMiddleware : IHttpMiddleware
    {
        private const int DefaultHistoryCapacity = 1000;
        private const int DefaultMaxCaptureSizeBytes = 5 * 1024 * 1024; // 5 MB
        private const int DefaultBinaryPreviewBytes = 64 * 1024; // 64 KB
        private static readonly TimeSpan CaptureErrorLogCooldown = TimeSpan.FromSeconds(30);

        private static readonly object HistoryLock = new object();
        private static HttpMonitorEvent[] _historyBuffer = new HttpMonitorEvent[DefaultHistoryCapacity];
        private static int _historyStart;
        private static int _historyCount;
        private static int _historyCapacity = DefaultHistoryCapacity;
        private static int _maxCaptureSizeBytes = DefaultMaxCaptureSizeBytes;
        private static int _binaryPreviewBytes = DefaultBinaryPreviewBytes;
        private static int _captureEnabled = 1;
        private static DateTime _nextCaptureErrorLogUtc = DateTime.MinValue;
        private static int _suppressedCaptureErrorCount;

        private struct BodySnapshot
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
        /// </summary>
        public static event Action<HttpMonitorEvent> OnRequestCaptured;

        /// <summary>
        /// Optional hook for value-level header transformations (for example redaction).
        /// Disabled by default.
        /// </summary>
        public static Func<string, string, string> HeaderValueTransform { get; set; }

        /// <summary>
        /// Optional diagnostics sink used for throttled capture-failure logs.
        /// </summary>
        public static Action<string> DiagnosticLogger { get; set; }

        public static bool CaptureEnabled
        {
            get => Volatile.Read(ref _captureEnabled) == 1;
            set => Volatile.Write(ref _captureEnabled, value ? 1 : 0);
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
            get => Volatile.Read(ref _historyCapacity);
            set
            {
                var clamped = Math.Max(10, value);
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
            set => Volatile.Write(ref _maxCaptureSizeBytes, Math.Max(1024, value));
        }

        public static int BinaryPreviewBytes
        {
            get => Volatile.Read(ref _binaryPreviewBytes);
            set => Volatile.Write(ref _binaryPreviewBytes, Math.Max(256, value));
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
                _historyStart = 0;
                _historyCount = 0;
            }

            PublishEvent(null);
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (!CaptureEnabled)
            {
                return await next(request, context, cancellationToken).ConfigureAwait(false);
            }

            UHttpResponse response = null;
            Exception exception = null;

            try
            {
                response = await next(request, context, cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                try
                {
                    var monitorEvent = BuildMonitorEvent(request, response, context, exception);
                    StoreEvent(monitorEvent);
                    PublishEvent(monitorEvent);
                }
                catch (Exception captureEx)
                {
                    LogCaptureFailure(captureEx);
                }
            }
        }

        private static HttpMonitorEvent BuildMonitorEvent(
            UHttpRequest request,
            UHttpResponse response,
            RequestContext context,
            Exception exception)
        {
            var requestHeaders = CopyHeaders(request?.Headers);
            var requestSnapshot = CreateBodySnapshot(request?.Body, requestHeaders);

            var responseHeaders = CopyHeaders(response?.Headers);
            var responseSnapshot = CreateBodySnapshot(response?.Body ?? ReadOnlyMemory<byte>.Empty, responseHeaders);

            var (error, errorType, failureKind) = ResolveError(response, exception);
            var statusCode = response != null ? (int)response.StatusCode : 0;
            var statusText = response != null ? response.StatusCode.ToString() : string.Empty;
            var elapsed = context != null ? context.Elapsed : (response?.ElapsedTime ?? TimeSpan.Zero);
            var timeline = CopyTimeline(context?.Timeline);

            return new HttpMonitorEvent(
                id: Guid.NewGuid().ToString("N"),
                timestamp: DateTime.UtcNow,
                method: request?.Method.ToString(),
                url: request?.Uri?.ToString(),
                requestHeaders: requestHeaders,
                requestBody: requestSnapshot.Body,
                originalRequestBodySize: requestSnapshot.OriginalSize,
                isRequestBodyTruncated: requestSnapshot.IsTruncated,
                isRequestBodyBinary: requestSnapshot.IsBinary,
                statusCode: statusCode,
                statusText: statusText,
                responseHeaders: responseHeaders,
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

        private static IReadOnlyDictionary<string, string> CopyHeaders(HttpHeaders headers)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return copy;
            }

            foreach (var header in headers)
            {
                var headerKey = header.Key ?? string.Empty;
                var value = header.Value ?? string.Empty;

                var transform = HeaderValueTransform;
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
            if (body.IsEmpty)
            {
                return BodySnapshot.Empty;
            }

            var originalSize = body.Length;
            var isBinary = HttpMonitorEvent.IsLikelyBinaryPayload(body, headers);
            var limit = isBinary ? BinaryPreviewBytes : MaxCaptureSizeBytes;
            var captureSize = Math.Min(originalSize, Math.Max(0, limit));
            var isTruncated = captureSize < originalSize;

            if (captureSize == 0)
            {
                return new BodySnapshot(ReadOnlyMemory<byte>.Empty, originalSize, true, isBinary);
            }

            var snapshot = new byte[captureSize];
            body.Slice(0, captureSize).CopyTo(snapshot.AsMemory());
            return new BodySnapshot(snapshot, originalSize, isTruncated, isBinary);
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
            UHttpResponse response,
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

            if (response?.Error != null)
            {
                var type = response.Error.Type;
                var failureKind = type == UHttpErrorType.HttpError
                    ? HttpMonitorFailureKind.HttpStatusError
                    : HttpMonitorFailureKind.TransportError;
                return (response.Error.ToString(), type, failureKind);
            }

            if (exception != null)
            {
                return (exception.Message ?? "Transport failure", null, HttpMonitorFailureKind.TransportError);
            }

            if (response != null && (int)response.StatusCode >= 400)
            {
                return (
                    $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                    UHttpErrorType.HttpError,
                    HttpMonitorFailureKind.HttpStatusError);
            }

            return (string.Empty, null, HttpMonitorFailureKind.None);
        }

        private static void StoreEvent(HttpMonitorEvent monitorEvent)
        {
            lock (HistoryLock)
            {
                if (_historyBuffer.Length != _historyCapacity)
                {
                    ResizeBufferLocked(_historyCapacity);
                }

                if (_historyCount < _historyBuffer.Length)
                {
                    var insertIndex = (_historyStart + _historyCount) % _historyBuffer.Length;
                    _historyBuffer[insertIndex] = monitorEvent;
                    _historyCount++;
                    return;
                }

                _historyBuffer[_historyStart] = monitorEvent;
                _historyStart = (_historyStart + 1) % _historyBuffer.Length;
            }
        }

        private static void ResizeBufferLocked(int newCapacity)
        {
            var capacity = Math.Max(10, newCapacity);
            var newBuffer = new HttpMonitorEvent[capacity];

            var itemsToCopy = Math.Min(_historyCount, capacity);
            var skipCount = _historyCount - itemsToCopy;

            for (int i = 0; i < itemsToCopy; i++)
            {
                var sourceIndex = (_historyStart + skipCount + i) % _historyBuffer.Length;
                newBuffer[i] = _historyBuffer[sourceIndex];
            }

            _historyBuffer = newBuffer;
            _historyStart = 0;
            _historyCount = itemsToCopy;
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

            lock (HistoryLock)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextCaptureErrorLogUtc)
                {
                    shouldLog = true;
                    suppressedCount = _suppressedCaptureErrorCount;
                    _suppressedCaptureErrorCount = 0;
                    _nextCaptureErrorLogUtc = now + CaptureErrorLogCooldown;
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
    }
}
