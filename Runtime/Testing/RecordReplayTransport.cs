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

        /// <summary>
        /// Whether a JSON body field is redacted. Matching ignores case and the separators
        /// <c>_</c>, <c>-</c>, <c>.</c> and spaces, so <c>access_token</c>, <c>accessToken</c> and
        /// <c>Access-Token</c> are the same name.
        /// </summary>
        public bool ShouldRedactJsonField(string fieldName)
        {
            return MatchesNormalized(JsonBodyFieldNames, fieldName);
        }

        /// <summary>
        /// Whether an <c>application/x-www-form-urlencoded</c> body field is redacted: it matches
        /// <see cref="JsonBodyFieldNames"/> or <see cref="QueryParameterNames"/> (format-insensitive,
        /// as in <see cref="ShouldRedactJsonField"/>).
        /// </summary>
        public bool ShouldRedactFormField(string fieldName)
        {
            return MatchesNormalized(JsonBodyFieldNames, fieldName)
                || MatchesNormalized(QueryParameterNames, fieldName);
        }

        private static bool MatchesNormalized(HashSet<string> names, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || names.Count == 0)
                return false;
            if (names.Contains(candidate))
                return true;

            var normalized = NormalizeFieldName(candidate);
            foreach (var name in names)
            {
                if (string.Equals(NormalizeFieldName(name), normalized, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string NormalizeFieldName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c == '_' || c == '-' || c == '.' || c == ' ')
                    continue;
                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
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
            "X-Amz-Security-Token",
            "X-Goog-Api-Key",
            "X-CSRF-Token",
            "X-XSRF-Token",
            "Proxy-Authenticate"
        };

        private static readonly string[] DefaultSensitiveQueryParameters =
        {
            "api_key",
            "apikey",
            "key",
            "token",
            "access_token",
            "refresh_token",
            "id_token",
            "client_secret",
            "code",
            "code_verifier",
            "signature",
            "sig",
            "auth",
            "password"
        };

        // OAuth 2.0 / OIDC names (RFC 6749 sections 4.1.3, 4.3.2, 5.1, 6; RFC 7636 section 4.5; OIDC
        // id_token). Matching is format-insensitive, so "accessToken" also covers "access_token".
        private static readonly string[] DefaultSensitiveJsonFields =
        {
            "password",
            "token",
            "access_token",
            "refresh_token",
            "id_token",
            "client_secret",
            "code",
            "code_verifier",
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

        /// <summary>
        /// Largest response body recorded, in bytes (default 16 MB). A larger response fails the request
        /// with a clear <see cref="UHttpException"/> instead of being truncated, and the recording is then
        /// incomplete: <see cref="RecordReplayTransport.SaveRecordings"/> throws and writes no file.
        /// </summary>
        public long MaxRecordBytes { get; set; } = RecordReplayTransport.DefaultMaxRecordBytes;
    }

    /// <summary>
    /// Transport wrapper that records HTTP interactions to disk or replays them deterministically.
    /// Uses schema-versioned DTOs and a stable request key for replay matching.
    /// </summary>
    public sealed partial class RecordReplayTransport : IHttpTransport
    {
        private const int RecordingSchemaVersion = 1;
        private const int KeySaltBytes = 32;
        private const int DrainChunkBytes = 16 * 1024;

        /// <summary>Default for <see cref="RecordReplayTransportOptions.MaxRecordBytes"/> (16 MB).</summary>
        public const long DefaultMaxRecordBytes = 16L * 1024 * 1024;
        private const int LargeBodyThresholdBytes = 1024 * 1024;
        private const int BodyEdgeSliceBytes = 64 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

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
        private readonly long _maxRecordBytes;
        // Per-recording salt for the HMAC of sensitive match-key header values and request body hashes.
        // Generated in Record mode, read from the file in Replay mode (null for older files: raw keys).
        private byte[] _keySalt;
        private string _recordingFailure;
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
            _maxRecordBytes = Math.Max(0L, options.MaxRecordBytes);

            // A header the caller names in MatchHeaderNames is matched even if excluded by default
            // (Authorization, Cookie, ...): its value enters the key only as a salted HMAC.
            if (options.MatchHeaderNames != null)
            {
                foreach (var headerName in options.MatchHeaderNames)
                {
                    if (!string.IsNullOrWhiteSpace(headerName))
                        _excludedMatchHeaders.Remove(headerName);
                }
            }

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
            else
            {
                _keySalt = new byte[KeySaltBytes];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(_keySalt);
                }
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

        [Obsolete("Use DispatchAsync or TransportDispatchHelper.CollectResponseAsync for the 22a handler pipeline.", false)]
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
                await DriveReplayHandlerAsync(safeHandler, entry, context).ConfigureAwait(false);
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

            var failure = Volatile.Read(ref _recordingFailure);
            if (failure != null)
            {
                throw new InvalidOperationException(
                    "RecordReplayTransport: the recording is incomplete and was not written. " + failure);
            }

            lock (_saveLock)
            {
                var entries = _recordedEntries
                    .ToArray()
                    .OrderBy(e => e.Sequence)
                    .ToList();

                var recordingFile = new RecordingFileDto
                {
                    Version = RecordingSchemaVersion,
                    KeySalt = _keySalt != null ? Convert.ToBase64String(_keySalt) : null,
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
                Log(ex.Message);
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

            var recordingHandler = new RecordingHandler(this, request, handler, cancellationToken);

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

            _keySalt = string.IsNullOrEmpty(recordingFile.KeySalt)
                ? null
                : Convert.FromBase64String(recordingFile.KeySalt);

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
            var requestBody = request.Content.TryGetBufferedData(out var bufferedRequestBody)
                ? bufferedRequestBody
                : ReadOnlyMemory<byte>.Empty;
            var requestHash = ComputeBodyHash(requestBody, _keySalt);
            var requestKey = BuildRequestKey(
                request.Method.ToUpperString(),
                NormalizeUriForKey(request.Uri),
                request.Headers,
                requestHash);

            var requestHeaders = ToDictionary(request.Headers, redact: true);
            var redactedRequestBody = RedactBodyIfNeeded(requestBody, request.Headers);

            var recordedResponseHeaders = responseHeaders != null
                ? ToDictionary(responseHeaders, redact: true)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var recordedResponseBody = !responseBody.IsEmpty
                ? RedactBodyIfNeeded(responseBody, responseHeaders)
                : null;

            var entry = new RecordingEntryDto
            {
                Sequence = Interlocked.Increment(ref _sequence),
                RequestKey = requestKey,
                Method = request.Method.ToUpperString(),
                Url = normalizedUrl,
                RequestHeaders = requestHeaders,
                RequestBodyHash = requestHash,
                RequestBodyBase64 = redactedRequestBody != null && redactedRequestBody.Length > 0
                    ? Convert.ToBase64String(redactedRequestBody)
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

        private static ValueTask DriveReplayHandlerAsync(
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
                return default;
            }

            var responseHeaders = FromDictionary(entry.ResponseHeaders);
            var statusCode = entry.StatusCode <= 0
                ? (int)HttpStatusCode.OK
                : entry.StatusCode;

            var body = string.IsNullOrEmpty(entry.ResponseBodyBase64)
                ? ReadOnlyMemory<byte>.Empty
                : new ReadOnlyMemory<byte>(Convert.FromBase64String(entry.ResponseBodyBase64));
            return handler.OnResponseStartAsync(
                statusCode,
                responseHeaders,
                new BufferedResponseBodySource(body, HttpHeaders.Empty),
                context);
        }
        private void MarkRecordingFailed(string message)
        {
            Interlocked.CompareExchange(ref _recordingFailure, message, null);
            Log(message);
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
            private readonly CancellationToken _cancellationToken;
            private SegmentedBuffer _responseBody;
            private UHttpRequest _request;
            private HttpHeaders _responseHeaders;
            private int _statusCode;
            private bool _requestStarted;

            public RecordingHandler(
                RecordReplayTransport owner,
                UHttpRequest request,
                IHttpHandler inner,
                CancellationToken cancellationToken)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _request = request ?? throw new ArgumentNullException(nameof(request));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _cancellationToken = cancellationToken;
            }

            public bool TerminalCallbackSeen { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _request = request ?? _request;
                _requestStarted = true;
                _inner.OnRequestStart(_request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                EnsureRequestStarted(context);
                _statusCode = statusCode;
                _responseHeaders = headers?.Clone() ?? new HttpHeaders();

                var bodyToForward = body;
                if (body != null)
                {
                    if (body.TryGetBufferedData(out var buffered))
                    {
                        if (buffered.Length > _owner._maxRecordBytes)
                        {
                            await DisposeSourceAsync(body, abort: true).ConfigureAwait(false);
                            FailOversize(buffered.Length, context);
                            return;
                        }

                        if (!buffered.IsEmpty)
                        {
                            if (_responseBody == null)
                                _responseBody = new SegmentedBuffer();

                            _responseBody.Write(buffered.Span);
                        }
                    }
                    else
                    {
                        // Streaming source (for example HTTP/1.1 over RawSocketTransport): drain it here,
                        // bounded by MaxRecordBytes, so the recording holds the whole body (#149), then
                        // hand the downstream handler a buffered copy. This handler owns the original source
                        // from here on and disposes it on every path.
                        HttpHeaders trailers;
                        try
                        {
                            var oversizeAt = await DrainIntoBufferAsync(body).ConfigureAwait(false);
                            if (oversizeAt > 0)
                            {
                                await DisposeSourceAsync(body, abort: true).ConfigureAwait(false);
                                FailOversize(oversizeAt, context);
                                return;
                            }

                            trailers = await body.GetTrailersAsync(_cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await DisposeSourceAsync(body, abort: true).ConfigureAwait(false);
                            DisposeBufferedBody();
                            throw;
                        }
                        catch (UHttpException ex)
                        {
                            await DisposeSourceAsync(body, abort: true).ConfigureAwait(false);
                            OnResponseError(ex, context);
                            return;
                        }
                        catch (Exception ex)
                        {
                            await DisposeSourceAsync(body, abort: true).ConfigureAwait(false);
                            OnResponseError(
                                new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex.Message, ex)),
                                context);
                            return;
                        }

                        await DisposeSourceAsync(body, abort: false).ConfigureAwait(false);
                        var copy = _responseBody != null
                            ? _responseBody.AsSequence().ToArray()
                            : Array.Empty<byte>();
                        bodyToForward = new BufferedResponseBodySource(copy, trailers);
                    }
                }

                TerminalCallbackSeen = true;

                try
                {
                    await _inner.OnResponseStartAsync(statusCode, headers, bodyToForward, context)
                        .ConfigureAwait(false);
                }
                catch
                {
                    DisposeBufferedBody();
                    throw;
                }

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

            // Returns 0 when the body ended within MaxRecordBytes, else the byte count that exceeded it.
            private async ValueTask<long> DrainIntoBufferAsync(IResponseBodySource body)
            {
                var max = _owner._maxRecordBytes;
                var chunk = ArrayPool<byte>.Shared.Rent(DrainChunkBytes);
                try
                {
                    long total = 0;
                    while (true)
                    {
                        var read = await body.ReadAsync(chunk.AsMemory(0, DrainChunkBytes), _cancellationToken)
                            .ConfigureAwait(false);
                        if (read <= 0)
                            return 0;

                        total += read;
                        if (total > max)
                            return total;

                        if (_responseBody == null)
                            _responseBody = new SegmentedBuffer();

                        _responseBody.Write(chunk.AsSpan(0, read));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                }
            }

            private void FailOversize(long observedBytes, RequestContext context)
            {
                DisposeBufferedBody();
                TerminalCallbackSeen = true;

                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Response body of {0} {1} exceeds RecordReplayTransportOptions.MaxRecordBytes ({2} bytes; " +
                    "at least {3} bytes received). Recordings are never truncated: raise MaxRecordBytes " +
                    "or exclude this request from recording.",
                    _request.Method.ToUpperString(),
                    _owner.NormalizeUriForStorage(_request.Uri),
                    _owner._maxRecordBytes,
                    observedBytes);
                _owner.MarkRecordingFailed(message);
                _inner.OnResponseError(
                    new UHttpException(new UHttpError(UHttpErrorType.Unknown, message)),
                    context);
            }

            private static async ValueTask DisposeSourceAsync(IResponseBodySource body, bool abort)
            {
                try
                {
                    if (abort)
                        body.Abort();
                    await body.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TurboHTTP] RecordReplayTransport body dispose failed: {ex.GetType().Name}");
                }
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
            public string KeySalt { get; set; }
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
