using System;
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
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (context == null) throw new ArgumentNullException(nameof(context));
            ThrowIfDisposed();

            cancellationToken.ThrowIfCancellationRequested();

            switch (_mode)
            {
                case RecordReplayMode.Record:
                    return await SendAndRecordAsync(request, context, cancellationToken).ConfigureAwait(false);

                case RecordReplayMode.Replay:
                    return await ReplayAsync(request, context, cancellationToken).ConfigureAwait(false);

                case RecordReplayMode.Passthrough:
                default:
                    if (_innerTransport == null)
                        throw new InvalidOperationException("No inner transport configured for passthrough mode.");
                    return await _innerTransport.SendAsync(request, context, cancellationToken).ConfigureAwait(false);
            }
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
                throw disposeException;
        }

        private async ValueTask<UHttpResponse> SendAndRecordAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (_innerTransport == null)
                throw new InvalidOperationException("Record mode requires an inner transport.");

            try
            {
                var response = await _innerTransport.SendAsync(request, context, cancellationToken).ConfigureAwait(false);
                RecordInteraction(request, response, null, threwException: false);
                return response;
            }
            catch (UHttpException ex)
            {
                RecordInteraction(request, response: null, ex.HttpError, threwException: true);
                throw;
            }
            catch (Exception ex)
            {
                var unknownError = new UHttpError(UHttpErrorType.Unknown, ex.Message, ex);
                RecordInteraction(request, response: null, unknownError, threwException: true);
                throw;
            }
        }

        private ValueTask<UHttpResponse> ReplayAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var strictKey = BuildRequestKey(request);
            if (TryDequeue(_replayByRequestKey, strictKey, out var strictEntry))
            {
                return new ValueTask<UHttpResponse>(BuildReplayResponse(strictEntry, request, context));
            }

            if (_mismatchPolicy == RecordReplayMismatchPolicy.Relaxed)
            {
                var relaxedKey = BuildRelaxedKey(request.Method.ToUpperString(), NormalizeUriForKey(request.Uri));
                if (TryDequeue(_replayByRelaxedKey, relaxedKey, out var relaxedEntry))
                {
                    Log($"RecordReplay mismatch relaxed: using relaxed key match for '{request.Method} {request.Uri}'.");
                    return new ValueTask<UHttpResponse>(BuildReplayResponse(relaxedEntry, request, context));
                }
            }

            var message =
                $"No replay recording matched request key '{strictKey}'. " +
                $"Mode={_mode}, MismatchPolicy={_mismatchPolicy}, RecordingPath='{_recordingPath}'.";

            if (_mismatchPolicy == RecordReplayMismatchPolicy.Warn)
            {
                Log(message);
                if (_innerTransport != null)
                {
                    return _innerTransport.SendAsync(request, context, cancellationToken);
                }
            }
            else if (_mismatchPolicy == RecordReplayMismatchPolicy.Relaxed && _innerTransport != null)
            {
                Log(message + " Falling back to inner transport due to Relaxed policy.");
                return _innerTransport.SendAsync(request, context, cancellationToken);
            }

            throw new InvalidOperationException(message);
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
            UHttpResponse response,
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

            var responseHeaders = response != null
                ? ToDictionary(response.Headers, redact: true)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var responseBody = response != null
                ? RedactJsonBodyIfNeeded(response.Body, response.Headers)
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
                StatusCode = response != null ? (int)response.StatusCode : 0,
                ResponseHeaders = responseHeaders,
                ResponseBodyBase64 = responseBody != null && responseBody.Length > 0
                    ? Convert.ToBase64String(responseBody)
                    : null,
                Error = ToErrorDto(error ?? response?.Error),
                ThrowsException = threwException,
                TimestampUtcTicks = DateTime.UtcNow.Ticks
            };

            _recordedEntries.Enqueue(entry);
        }

        private UHttpResponse BuildReplayResponse(
            RecordingEntryDto entry,
            UHttpRequest request,
            RequestContext context)
        {
            if (entry.ThrowsException && entry.Error != null)
            {
                throw new UHttpException(ToError(entry.Error));
            }

            var responseHeaders = FromDictionary(entry.ResponseHeaders);
            var responseBody = string.IsNullOrEmpty(entry.ResponseBodyBase64)
                ? ReadOnlyMemory<byte>.Empty
                : Convert.FromBase64String(entry.ResponseBodyBase64);

            var statusCode = entry.StatusCode <= 0
                ? HttpStatusCode.OK
                : (HttpStatusCode)entry.StatusCode;

            var error = ToError(entry.Error);
            return new UHttpResponse(
                statusCode,
                responseHeaders,
                responseBody,
                context.Elapsed,
                request,
                error);
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
