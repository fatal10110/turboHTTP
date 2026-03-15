using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Testing
{
    public enum RecordReplayMode
    {
        Record,
        Replay,
        Passthrough
    }

    public enum RecordReplayMismatchPolicy
    {
        Strict,
        Warn,
        Relaxed
    }

    public sealed class RecordReplayRedactionPolicy
    {
        public static readonly string DefaultRedactedValue = "[REDACTED]";

        public HashSet<string> HeaderNames { get; }
        public HashSet<string> QueryParameterNames { get; }
        public HashSet<string> JsonBodyFieldNames { get; }
        public string RedactedValue { get; set; } = DefaultRedactedValue;

        public RecordReplayRedactionPolicy(
            IEnumerable<string> headerNames = null,
            IEnumerable<string> queryParameterNames = null,
            IEnumerable<string> jsonBodyFieldNames = null)
        {
            HeaderNames = new HashSet<string>(
                headerNames ?? DefaultSensitiveHeaders,
                StringComparer.OrdinalIgnoreCase);
            QueryParameterNames = new HashSet<string>(
                queryParameterNames ?? DefaultSensitiveQueryParameters,
                StringComparer.OrdinalIgnoreCase);
            JsonBodyFieldNames = new HashSet<string>(
                jsonBodyFieldNames ?? DefaultSensitiveJsonFields,
                StringComparer.OrdinalIgnoreCase);
        }

        public static RecordReplayRedactionPolicy CreateDefault()
        {
            return new RecordReplayRedactionPolicy();
        }

        public bool ShouldRedactHeader(string headerName)
        {
            return !string.IsNullOrWhiteSpace(headerName) && HeaderNames.Contains(headerName);
        }

        public bool ShouldRedactQueryParameter(string queryKey)
        {
            return !string.IsNullOrWhiteSpace(queryKey) && QueryParameterNames.Contains(queryKey);
        }

        public bool ShouldRedactJsonField(string fieldName)
        {
            return !string.IsNullOrWhiteSpace(fieldName) && JsonBodyFieldNames.Contains(fieldName);
        }

        private static readonly string[] DefaultSensitiveHeaders =
        {
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "WWW-Authenticate",
            "X-Api-Key",
            "Api-Key",
            "X-Auth-Token",
            "X-Amz-Security-Token"
        };

        private static readonly string[] DefaultSensitiveQueryParameters =
        {
            "api_key",
            "apikey",
            "key",
            "token",
            "access_token",
            "refresh_token",
            "signature",
            "sig",
            "auth",
            "password"
        };

        private static readonly string[] DefaultSensitiveJsonFields =
        {
            "password",
            "token",
            "accessToken",
            "refreshToken",
            "authorization",
            "apiKey",
            "secret",
            "cookie"
        };
    }

    public sealed class RecordReplayTransportOptions
    {
        public RecordReplayMode Mode { get; set; } = RecordReplayMode.Passthrough;
        public string RecordingPath { get; set; }
        public RecordReplayMismatchPolicy MismatchPolicy { get; set; } = RecordReplayMismatchPolicy.Strict;
        public RecordReplayRedactionPolicy RedactionPolicy { get; set; } = RecordReplayRedactionPolicy.CreateDefault();
        public bool AutoFlushOnDispose { get; set; } = true;
        public Action<string> Logger { get; set; }
        public IEnumerable<string> MatchHeaderNames { get; set; }
        public IEnumerable<string> ExcludedMatchHeaderNames { get; set; }
    }

    /// <summary>
    /// Transport wrapper that records HTTP interactions to disk or replays them deterministically.
    /// Uses schema-versioned DTOs and a stable request key for replay matching.
    /// </summary>
    public sealed partial class RecordReplayTransport : IHttpTransport
    {
        private const int RecordingSchemaVersion = 1;
        private const int LargeBodyThresholdBytes = 1024 * 1024;
        private const int BodyEdgeSliceBytes = 64 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private static readonly string[] DefaultMatchHeaders =
        {
            "Accept",
            "Accept-Encoding",
            "Content-Type",
            "Content-Encoding"
        };

        private static readonly string[] DefaultExcludedMatchHeaders =
        {
            "Date",
            "Age",
            "X-Request-ID",
            "X-Correlation-ID",
            "X-Trace-ID",
            "X-Span-ID",
            "Traceparent",
            "Tracestate",
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "WWW-Authenticate"
        };

        private readonly IHttpTransport _innerTransport;
        private readonly RecordReplayMode _mode;
        private readonly string _recordingPath;
        private readonly RecordReplayMismatchPolicy _mismatchPolicy;
        private readonly RecordReplayRedactionPolicy _redactionPolicy;
        private readonly bool _autoFlushOnDispose;
        private readonly Action<string> _logger;
        private readonly HashSet<string> _matchHeaders;
        private readonly HashSet<string> _excludedMatchHeaders;
        private readonly ConcurrentQueue<RecordingEntryDto> _recordedEntries = new ConcurrentQueue<RecordingEntryDto>();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>> _replayByRequestKey =
            new ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>> _replayByRelaxedKey =
            new ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>>(StringComparer.Ordinal);
        private readonly object _saveLock = new object();

        private int _disposed;
        private long _sequence;
        private long _createdUtcTicks = DateTime.UtcNow.Ticks;

        private sealed class ReplayNode
        {
            private int _consumed;
            public RecordingEntryDto Entry { get; }

            public ReplayNode(RecordingEntryDto entry)
            {
                Entry = entry;
            }

            public bool TryConsume()
            {
                return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
            }
        }

        public RecordReplayTransport(IHttpTransport innerTransport, RecordReplayTransportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            _innerTransport = innerTransport;
            _mode = options.Mode;
            _recordingPath = options.RecordingPath;
            _mismatchPolicy = options.MismatchPolicy;
            _redactionPolicy = options.RedactionPolicy ?? RecordReplayRedactionPolicy.CreateDefault();
            _autoFlushOnDispose = options.AutoFlushOnDispose;
            _logger = options.Logger;
            _matchHeaders = new HashSet<string>(
                options.MatchHeaderNames ?? DefaultMatchHeaders,
                StringComparer.OrdinalIgnoreCase);
            _excludedMatchHeaders = new HashSet<string>(
                DefaultExcludedMatchHeaders,
                StringComparer.OrdinalIgnoreCase);

            if (options.ExcludedMatchHeaderNames != null)
            {
                foreach (var headerName in options.ExcludedMatchHeaderNames)
                {
                    if (!string.IsNullOrWhiteSpace(headerName))
                        _excludedMatchHeaders.Add(headerName);
                }
            }

            if (_mode != RecordReplayMode.Passthrough && string.IsNullOrWhiteSpace(_recordingPath))
            {
                throw new ArgumentException(
                    "RecordingPath is required when mode is Record or Replay.",
                    nameof(options));
            }

            if (_mode != RecordReplayMode.Replay && _innerTransport == null)
            {
                throw new ArgumentNullException(
                    nameof(innerTransport),
                    "Inner transport is required for Record and Passthrough modes.");
            }

            if (_mode == RecordReplayMode.Replay)
            {
                LoadRecordings();
            }
        }

        public RecordReplayTransport(
            IHttpTransport innerTransport,
            string recordingPath,
            RecordReplayMode mode = RecordReplayMode.Passthrough)
            : this(
                  innerTransport,
                  new RecordReplayTransportOptions
                  {
                      Mode = mode,
                      RecordingPath = recordingPath
                  })
        {
        }

        public async ValueTask<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return await TransportDispatchHelper
                .CollectResponseAsync(this, request, context, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (context == null) throw new ArgumentNullException(nameof(context));
            ThrowIfDisposed();

            context.SetState(TransportBehaviorFlags.SelfDrainsResponseBody, true);
            var safeHandler = HandlerCallbackSafetyWrapper.Wrap(handler, context);
            if (_mode == RecordReplayMode.Passthrough)
            {
                if (_innerTransport == null)
                    throw new InvalidOperationException("No inner transport configured for passthrough mode.");

                await _innerTransport.DispatchAsync(request, safeHandler, context, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (_mode == RecordReplayMode.Record)
            {
                await DispatchRecordAsync(request, safeHandler, context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (TryResolveReplay(request, out var entry, out var fallbackToInner, out var mismatchMessage))
            {
                safeHandler.OnRequestStart(request, context);
                cancellationToken.ThrowIfCancellationRequested();
                DriveReplayHandler(safeHandler, entry, context);
                return;
            }

            if (fallbackToInner)
            {
                await _innerTransport.DispatchAsync(request, safeHandler, context, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            safeHandler.OnRequestStart(request, context);
            cancellationToken.ThrowIfCancellationRequested();
            safeHandler.OnResponseError(new UHttpException(
                new UHttpError(UHttpErrorType.Unknown, mismatchMessage)), context);
        }

        public void SaveRecordings()
        {
            if (_mode != RecordReplayMode.Record)
                return;

            lock (_saveLock)
            {
                var entries = _recordedEntries
                    .ToArray()
                    .OrderBy(e => e.Sequence)
                    .ToList();

                var recordingFile = new RecordingFileDto
                {
                    Version = RecordingSchemaVersion,
                    CreatedUtcTicks = _createdUtcTicks,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                    Entries = entries
                };

                var serialized = ToSerializableDictionary(recordingFile);
                var json = SerializeJson(serialized, typeof(Dictionary<string, object>));
                var directory = Path.GetDirectoryName(_recordingPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_recordingPath, json, Utf8);
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Exception disposeException = null;

            try
            {
                if (_mode == RecordReplayMode.Record && _autoFlushOnDispose)
                {
                    SaveRecordings();
                }
            }
            catch (Exception ex)
            {
                disposeException = ex;
            }

            try
            {
                _innerTransport?.Dispose();
            }
            catch (Exception ex)
            {
                disposeException = disposeException == null
                    ? ex
                    : new AggregateException(disposeException, ex);
            }

            if (disposeException != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TurboHTTP] RecordReplayTransport dispose error: {disposeException}");
            }
        }

        private async Task DispatchRecordAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (_innerTransport == null)
                throw new InvalidOperationException("Record mode requires an inner transport.");

            var recordingHandler = new RecordingHandler(this, request, handler);

            try
            {
                await _innerTransport.DispatchAsync(request, recordingHandler, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                recordingHandler.DisposeBufferedBody();
                throw;
            }
            catch (UHttpException ex) when (ex.HttpError != null && ex.HttpError.Type == UHttpErrorType.Cancelled)
            {
                recordingHandler.DisposeBufferedBody();
                throw new OperationCanceledException(ex.HttpError.Message, ex, cancellationToken);
            }
            catch (UHttpException ex)
            {
                if (!recordingHandler.TerminalCallbackSeen)
                {
                    recordingHandler.OnResponseError(ex, context);
                    return;
                }

                throw;
            }
            catch (Exception ex)
            {
                if (!recordingHandler.TerminalCallbackSeen)
                {
                    recordingHandler.OnResponseError(new UHttpException(
                        new UHttpError(UHttpErrorType.Unknown, ex.Message, ex)), context);
                    return;
                }

                throw;
            }
        }

        private bool TryResolveReplay(
            UHttpRequest request,
            out RecordingEntryDto entry,
            out bool fallbackToInner,
            out string mismatchMessage)
        {
            entry = null;
            fallbackToInner = false;
            mismatchMessage = null;

            var strictKey = BuildRequestKey(request);
            if (TryDequeue(_replayByRequestKey, strictKey, out entry))
            {
                return true;
            }

            if (_mismatchPolicy == RecordReplayMismatchPolicy.Relaxed)
            {
                var relaxedKey = BuildRelaxedKey(request.Method.ToUpperString(), NormalizeUriForKey(request.Uri));
                if (TryDequeue(_replayByRelaxedKey, relaxedKey, out entry))
                {
                    Log($"RecordReplay mismatch relaxed: using relaxed key match for '{request.Method} {request.Uri}'.");
                    return true;
                }
            }

            mismatchMessage =
                $"No replay recording matched request key '{strictKey}'. " +
                $"Mode={_mode}, MismatchPolicy={_mismatchPolicy}, RecordingPath='{_recordingPath}'.";

            if (_mismatchPolicy == RecordReplayMismatchPolicy.Warn && _innerTransport != null)
            {
                Log(mismatchMessage);
                fallbackToInner = true;
            }
            else if (_mismatchPolicy == RecordReplayMismatchPolicy.Relaxed && _innerTransport != null)
            {
                Log(mismatchMessage + " Falling back to inner transport due to Relaxed policy.");
                fallbackToInner = true;
            }

            return false;
        }

        private void LoadRecordings()
        {
            if (!File.Exists(_recordingPath))
            {
                throw new FileNotFoundException(
                    $"Recording file was not found for replay mode: '{_recordingPath}'.",
                    _recordingPath);
            }

            var json = File.ReadAllText(_recordingPath, Utf8);
            var rawObject = DeserializeJson(json, typeof(Dictionary<string, object>));
            var recordingFile = FromSerializableObject(rawObject);
            if (recordingFile == null)
                throw new InvalidOperationException("Recording file deserialized to null.");

            if (recordingFile.Version != RecordingSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported recording schema version {recordingFile.Version}. " +
                    $"Expected {RecordingSchemaVersion}.");
            }

            _createdUtcTicks = recordingFile.CreatedUtcTicks > 0
                ? recordingFile.CreatedUtcTicks
                : DateTime.UtcNow.Ticks;

            if (recordingFile.Entries == null || recordingFile.Entries.Count == 0)
                return;

            foreach (var entry in recordingFile.Entries.OrderBy(e => e.Sequence))
            {
                var requestKey = string.IsNullOrEmpty(entry.RequestKey)
                    ? BuildRequestKeyFromEntry(entry)
                    : entry.RequestKey;

                var node = new ReplayNode(entry);
                _replayByRequestKey
                    .GetOrAdd(requestKey, _ => new ConcurrentQueue<ReplayNode>())
                    .Enqueue(node);

                var relaxedKey = BuildRelaxedKey(entry.Method, entry.Url);
                _replayByRelaxedKey
                    .GetOrAdd(relaxedKey, _ => new ConcurrentQueue<ReplayNode>())
                    .Enqueue(node);
            }
        }

        private void RecordInteraction(
            UHttpRequest request,
            HttpStatusCode? statusCode,
            HttpHeaders responseHeaders,
            ReadOnlySequence<byte> responseBody,
            UHttpError error,
            bool threwException)
        {
            var normalizedUrl = NormalizeUriForStorage(request.Uri);
            var requestHash = ComputeBodyHash(request.Body);
            var requestKey = BuildRequestKey(
                request.Method.ToUpperString(),
                NormalizeUriForKey(request.Uri),
                request.Headers,
                requestHash);

            var requestHeaders = ToDictionary(request.Headers, redact: true);
            var requestBody = RedactJsonBodyIfNeeded(request.Body, request.Headers);

            var recordedResponseHeaders = responseHeaders != null
                ? ToDictionary(responseHeaders, redact: true)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var recordedResponseBody = !responseBody.IsEmpty
                ? RedactJsonBodyIfNeeded(responseBody, responseHeaders)
                : null;

            var entry = new RecordingEntryDto
            {
                Sequence = Interlocked.Increment(ref _sequence),
                RequestKey = requestKey,
                Method = request.Method.ToUpperString(),
                Url = normalizedUrl,
                RequestHeaders = requestHeaders,
                RequestBodyHash = requestHash,
                RequestBodyBase64 = requestBody != null && requestBody.Length > 0
                    ? Convert.ToBase64String(requestBody)
                    : null,
                StatusCode = statusCode.HasValue ? (int)statusCode.Value : 0,
                ResponseHeaders = recordedResponseHeaders,
                ResponseBodyBase64 = recordedResponseBody != null && recordedResponseBody.Length > 0
                    ? Convert.ToBase64String(recordedResponseBody)
                    : null,
                Error = ToErrorDto(error),
                ThrowsException = threwException,
                TimestampUtcTicks = DateTime.UtcNow.Ticks
            };

            _recordedEntries.Enqueue(entry);
        }

        private static void DriveReplayHandler(
            IHttpHandler handler,
            RecordingEntryDto entry,
            RequestContext context)
        {
            if (entry == null)
                throw new InvalidOperationException("Replay transport produced a null recording entry.");

            var replayError = ToError(entry.Error);
            if (entry.ThrowsException || replayError != null)
            {
                handler.OnResponseError(new UHttpException(
                    replayError ?? new UHttpError(
                        UHttpErrorType.Unknown,
                        "Replay entry was marked as failed but did not include an error.")),
                    context);
                return;
            }

            var responseHeaders = FromDictionary(entry.ResponseHeaders);
            var statusCode = entry.StatusCode <= 0
                ? (int)HttpStatusCode.OK
                : entry.StatusCode;

            handler.OnResponseStart(statusCode, responseHeaders, context);

            if (!string.IsNullOrEmpty(entry.ResponseBodyBase64))
            {
                var body = Convert.FromBase64String(entry.ResponseBodyBase64);
                if (body.Length > 0)
                    handler.OnResponseData(body, context);
            }

            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }
        private void Log(string message)
        {
            _logger?.Invoke(message);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RecordReplayTransport));
        }

        private sealed class RecordingHandler : IHttpHandler
        {
            private readonly RecordReplayTransport _owner;
            private readonly IHttpHandler _inner;
            private SegmentedBuffer _responseBody;
            private UHttpRequest _request;
            private HttpHeaders _responseHeaders;
            private int _statusCode;
            private bool _requestStarted;

            public RecordingHandler(
                RecordReplayTransport owner,
                UHttpRequest request,
                IHttpHandler inner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _request = request ?? throw new ArgumentNullException(nameof(request));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public bool TerminalCallbackSeen { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _request = request ?? _request;
                _requestStarted = true;
                _inner.OnRequestStart(_request, context);
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                EnsureRequestStarted(context);
                _statusCode = statusCode;
                _responseHeaders = headers?.Clone() ?? new HttpHeaders();
                _inner.OnResponseStart(statusCode, headers, context);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                if (!chunk.IsEmpty)
                {
                    if (_responseBody == null)
                        _responseBody = new SegmentedBuffer();

                    _responseBody.Write(chunk);
                }

                _inner.OnResponseData(chunk, context);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                EnsureRequestStarted(context);
                TerminalCallbackSeen = true;

                try
                {
                    _owner.RecordInteraction(
                        _request,
                        (HttpStatusCode)_statusCode,
                        _responseHeaders,
                        _responseBody?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                        error: null,
                        threwException: false);
                }
                finally
                {
                    DisposeBufferedBody();
                }

                _inner.OnResponseEnd(trailers, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                EnsureRequestStarted(context);
                TerminalCallbackSeen = true;

                try
                {
                    _owner.RecordInteraction(
                        _request,
                        _responseHeaders != null ? (HttpStatusCode?)_statusCode : null,
                        _responseHeaders,
                        _responseBody?.AsSequence() ?? ReadOnlySequence<byte>.Empty,
                        error?.HttpError,
                        threwException: true);
                }
                finally
                {
                    DisposeBufferedBody();
                }

                _inner.OnResponseError(error, context);
            }

            public void DisposeBufferedBody()
            {
                _responseBody?.Dispose();
                _responseBody = null;
            }

            private void EnsureRequestStarted(RequestContext context)
            {
                if (_requestStarted)
                    return;

                _requestStarted = true;
                _inner.OnRequestStart(_request, context);
            }
        }

        [Serializable]
        private sealed class RecordingFileDto
        {
            public int Version { get; set; }
            public long CreatedUtcTicks { get; set; }
            public long UpdatedUtcTicks { get; set; }
            public List<RecordingEntryDto> Entries { get; set; } = new List<RecordingEntryDto>();
        }

        [Serializable]
        private sealed class RecordingEntryDto
        {
            public long Sequence { get; set; }
            public string RequestKey { get; set; }
            public string Method { get; set; }
            public string Url { get; set; }
            public Dictionary<string, List<string>> RequestHeaders { get; set; }
            public string RequestBodyHash { get; set; }
            public string RequestBodyBase64 { get; set; }
            public int StatusCode { get; set; }
            public Dictionary<string, List<string>> ResponseHeaders { get; set; }
            public string ResponseBodyBase64 { get; set; }
            public RecordingErrorDto Error { get; set; }
            public bool ThrowsException { get; set; }
            public long TimestampUtcTicks { get; set; }
        }

        [Serializable]
        private sealed class RecordingErrorDto
        {
            public string Type { get; set; }
            public string Message { get; set; }
            public int? StatusCode { get; set; }
        }
    }
}
