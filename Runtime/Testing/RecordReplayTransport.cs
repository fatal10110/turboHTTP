using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
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
    public sealed class RecordReplayTransport : IHttpTransport
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

        public async Task<UHttpResponse> SendAsync(
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

        private async Task<UHttpResponse> SendAndRecordAsync(
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

        private Task<UHttpResponse> ReplayAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var strictKey = BuildRequestKey(request);
            if (TryDequeue(_replayByRequestKey, strictKey, out var strictEntry))
            {
                return Task.FromResult(BuildReplayResponse(strictEntry, request, context));
            }

            if (_mismatchPolicy == RecordReplayMismatchPolicy.Relaxed)
            {
                var relaxedKey = BuildRelaxedKey(request.Method.ToUpperString(), NormalizeUriForKey(request.Uri));
                if (TryDequeue(_replayByRelaxedKey, relaxedKey, out var relaxedEntry))
                {
                    Log($"RecordReplay mismatch relaxed: using relaxed key match for '{request.Method} {request.Uri}'.");
                    return Task.FromResult(BuildReplayResponse(relaxedEntry, request, context));
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
                ? null
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

        private string BuildRequestKey(UHttpRequest request)
        {
            var normalizedUrl = NormalizeUriForKey(request.Uri);
            var bodyHash = ComputeBodyHash(request.Body);
            return BuildRequestKey(request.Method.ToUpperString(), normalizedUrl, request.Headers, bodyHash);
        }

        private string BuildRequestKeyFromEntry(RecordingEntryDto entry)
        {
            var keyHeaders = entry.RequestHeaders ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            return BuildRequestKey(
                entry.Method,
                entry.Url,
                keyHeaders,
                entry.RequestBodyHash ?? "sha256:empty");
        }

        private string BuildRequestKey(string method, string normalizedUrl, HttpHeaders headers, string bodyHash)
        {
            return BuildRequestKey(method, normalizedUrl, ToDictionary(headers, redact: false), bodyHash);
        }

        private string BuildRequestKey(
            string method,
            string normalizedUrl,
            Dictionary<string, List<string>> headers,
            string bodyHash)
        {
            var normalizedMethod = (method ?? string.Empty).Trim().ToUpperInvariant();
            var headerSignature = BuildHeaderSignature(headers);
            return string.Concat(
                normalizedMethod,
                "|",
                normalizedUrl ?? string.Empty,
                "|",
                headerSignature,
                "|",
                bodyHash ?? "sha256:empty");
        }

        private static string BuildRelaxedKey(string method, string normalizedUrl)
        {
            return string.Concat(
                (method ?? string.Empty).Trim().ToUpperInvariant(),
                "|",
                normalizedUrl ?? string.Empty);
        }

        private string BuildHeaderSignature(Dictionary<string, List<string>> headers)
        {
            if (headers == null || headers.Count == 0)
                return string.Empty;

            var builder = new StringBuilder(128);
            var names = headers.Keys
                .Where(ShouldIncludeHeaderInMatchKey)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                if (!headers.TryGetValue(name, out var values) || values == null || values.Count == 0)
                    continue;

                builder.Append(name.ToLowerInvariant());
                builder.Append('=');
                var sortedValues = values.OrderBy(v => v ?? string.Empty, StringComparer.Ordinal).ToArray();
                for (int i = 0; i < sortedValues.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(sortedValues[i] ?? string.Empty);
                }
                builder.Append(';');
            }

            return builder.ToString();
        }

        private bool ShouldIncludeHeaderInMatchKey(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName))
                return false;
            if (_excludedMatchHeaders.Contains(headerName))
                return false;
            return _matchHeaders.Contains(headerName);
        }

        private string NormalizeUriForStorage(Uri uri)
        {
            return NormalizeUri(uri, redactSensitiveQueryValues: true);
        }

        private string NormalizeUriForKey(Uri uri)
        {
            return NormalizeUri(uri, redactSensitiveQueryValues: true);
        }

        private string NormalizeUri(Uri uri, bool redactSensitiveQueryValues)
        {
            if (uri == null)
                return string.Empty;

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host.ToLowerInvariant();
            var defaultPort = (scheme == Uri.UriSchemeHttp && uri.Port == 80) ||
                              (scheme == Uri.UriSchemeHttps && uri.Port == 443);
            var authority = defaultPort
                ? host
                : host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            var normalizedQuery = NormalizeQuery(uri.Query, redactSensitiveQueryValues);

            return string.Concat(
                scheme,
                "://",
                authority,
                path,
                normalizedQuery);
        }

        private string NormalizeQuery(string query, bool redactSensitiveQueryValues)
        {
            if (string.IsNullOrEmpty(query))
                return string.Empty;

            var trimmed = query[0] == '?' ? query.Substring(1) : query;
            if (trimmed.Length == 0)
                return string.Empty;

            var items = new List<KeyValuePair<string, string>>();
            var segments = trimmed.Split('&');
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                    continue;

                var equalsIndex = segment.IndexOf('=');
                string key;
                string value;
                if (equalsIndex < 0)
                {
                    key = segment;
                    value = string.Empty;
                }
                else
                {
                    key = segment.Substring(0, equalsIndex);
                    value = segment.Substring(equalsIndex + 1);
                }

                if (redactSensitiveQueryValues && _redactionPolicy.ShouldRedactQueryParameter(Uri.UnescapeDataString(key)))
                {
                    value = _redactionPolicy.RedactedValue;
                }

                items.Add(new KeyValuePair<string, string>(key, value));
            }

            items.Sort((a, b) =>
            {
                var keyCompare = StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
                if (keyCompare != 0) return keyCompare;
                return StringComparer.Ordinal.Compare(a.Value, b.Value);
            });

            if (items.Count == 0)
                return string.Empty;

            var builder = new StringBuilder(64);
            builder.Append('?');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) builder.Append('&');
                builder.Append(items[i].Key);
                if (!string.IsNullOrEmpty(items[i].Value))
                {
                    builder.Append('=');
                    builder.Append(items[i].Value);
                }
            }

            return builder.ToString();
        }

        private Dictionary<string, List<string>> ToDictionary(HttpHeaders headers, bool redact)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
                return result;

            foreach (var name in headers.Names)
            {
                var values = headers.GetValues(name);
                var copiedValues = new List<string>(values.Count);
                for (int i = 0; i < values.Count; i++)
                {
                    var value = values[i] ?? string.Empty;
                    if (redact && _redactionPolicy.ShouldRedactHeader(name))
                    {
                        copiedValues.Add(_redactionPolicy.RedactedValue);
                    }
                    else
                    {
                        copiedValues.Add(value);
                    }
                }
                result[name] = copiedValues;
            }

            return result;
        }

        private static HttpHeaders FromDictionary(Dictionary<string, List<string>> headers)
        {
            var result = new HttpHeaders();
            if (headers == null)
                return result;

            foreach (var pair in headers)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    result.Set(pair.Key, string.Empty);
                    continue;
                }

                result.Set(pair.Key, pair.Value[0] ?? string.Empty);
                for (int i = 1; i < pair.Value.Count; i++)
                {
                    result.Add(pair.Key, pair.Value[i] ?? string.Empty);
                }
            }

            return result;
        }

        private byte[] RedactJsonBodyIfNeeded(byte[] body, HttpHeaders headers)
        {
            if (body == null || body.Length == 0)
                return body;
            if (_redactionPolicy.JsonBodyFieldNames.Count == 0)
                return body;

            var contentType = headers?.Get("Content-Type");
            if (string.IsNullOrEmpty(contentType) ||
                contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return body;
            }

            object parsedBody;
            try
            {
                var json = Utf8.GetString(body);
                parsedBody = DeserializeJson(json, typeof(object));
            }
            catch
            {
                return body;
            }

            if (parsedBody == null)
                return body;

            if (!TryRedactParsedJson(parsedBody))
                return body;

            string redactedJson;
            try
            {
                redactedJson = SerializeJson(parsedBody, typeof(object));
            }
            catch
            {
                return body;
            }

            return Utf8.GetBytes(redactedJson);
        }

        private bool TryRedactParsedJson(object node)
        {
            if (node is Dictionary<string, object> objectDict)
            {
                var changed = false;
                var keys = objectDict.Keys.ToArray();
                for (int i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    if (_redactionPolicy.ShouldRedactJsonField(key))
                    {
                        objectDict[key] = _redactionPolicy.RedactedValue;
                        changed = true;
                        continue;
                    }

                    var value = objectDict[key];
                    if (value != null && TryRedactParsedJson(value))
                        changed = true;
                }
                return changed;
            }

            if (node is List<object> objectList)
            {
                var changed = false;
                for (int i = 0; i < objectList.Count; i++)
                {
                    var value = objectList[i];
                    if (value != null && TryRedactParsedJson(value))
                        changed = true;
                }
                return changed;
            }

            return false;
        }

        private static RecordingErrorDto ToErrorDto(UHttpError error)
        {
            if (error == null)
                return null;

            return new RecordingErrorDto
            {
                Type = error.Type.ToString(),
                Message = error.Message,
                StatusCode = error.StatusCode.HasValue ? (int?)error.StatusCode.Value : null
            };
        }

        private static UHttpError ToError(RecordingErrorDto error)
        {
            if (error == null)
                return null;

            if (!Enum.TryParse(error.Type, ignoreCase: true, out UHttpErrorType parsedType))
                parsedType = UHttpErrorType.Unknown;

            HttpStatusCode? statusCode = null;
            if (error.StatusCode.HasValue)
                statusCode = (HttpStatusCode)error.StatusCode.Value;

            return new UHttpError(parsedType, error.Message ?? "Unknown replay error", statusCode: statusCode);
        }

        private static bool TryDequeue(
            ConcurrentDictionary<string, ConcurrentQueue<ReplayNode>> source,
            string key,
            out RecordingEntryDto entry)
        {
            entry = null;
            if (!source.TryGetValue(key, out var queue))
                return false;

            while (queue.TryDequeue(out var node))
            {
                if (node.TryConsume())
                {
                    entry = node.Entry;
                    return true;
                }
            }

            return false;
        }

        private static string ComputeBodyHash(byte[] body)
        {
            if (body == null || body.Length == 0)
                return "sha256:empty";

            try
            {
                using var sha = SHA256.Create();
                if (sha == null)
                {
                    throw new InvalidOperationException(
                        "SHA-256 provider is unavailable. Preserve SHA256 types from stripping " +
                        "(see Runtime/Testing/link.xml) for IL2CPP builds.");
                }

                byte[] hash;
                if (body.Length > LargeBodyThresholdBytes)
                {
                    var firstLength = Math.Min(BodyEdgeSliceBytes, body.Length);
                    var lastLength = Math.Min(BodyEdgeSliceBytes, body.Length - firstLength);
                    sha.TransformBlock(body, 0, firstLength, null, 0);
                    if (lastLength > 0)
                    {
                        var lastOffset = body.Length - lastLength;
                        sha.TransformBlock(body, lastOffset, lastLength, null, 0);
                    }

                    var lengthBytes = BitConverter.GetBytes(body.LongLength);
                    if (!BitConverter.IsLittleEndian)
                        Array.Reverse(lengthBytes);
                    sha.TransformFinalBlock(lengthBytes, 0, lengthBytes.Length);
                    hash = sha.Hash;
                }
                else
                {
                    hash = sha.ComputeHash(body);
                }

                return "sha256:" + ToLowerHex(hash);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to compute request body hash with SHA-256. " +
                    "This usually means the hashing provider was stripped or unavailable on this platform. " +
                    "Add SHA256 preservation guidance from Runtime/Testing/link.xml.",
                    ex);
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            var chars = new char[bytes.Length * 2];
            int index = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[index++] = ToHexNibble(b >> 4);
                chars[index++] = ToHexNibble(b & 0x0F);
            }
            return new string(chars);
        }

        private static char ToHexNibble(int value)
        {
            return (char)(value < 10 ? ('0' + value) : ('a' + (value - 10)));
        }

        private static Dictionary<string, object> ToSerializableDictionary(RecordingFileDto file)
        {
            var entries = new List<object>(file.Entries.Count);
            for (int i = 0; i < file.Entries.Count; i++)
            {
                entries.Add(ToSerializableDictionary(file.Entries[i]));
            }

            return new Dictionary<string, object>
            {
                ["Version"] = file.Version,
                ["CreatedUtcTicks"] = file.CreatedUtcTicks,
                ["UpdatedUtcTicks"] = file.UpdatedUtcTicks,
                ["Entries"] = entries
            };
        }

        private static Dictionary<string, object> ToSerializableDictionary(RecordingEntryDto entry)
        {
            return new Dictionary<string, object>
            {
                ["Sequence"] = entry.Sequence,
                ["RequestKey"] = entry.RequestKey,
                ["Method"] = entry.Method,
                ["Url"] = entry.Url,
                ["RequestHeaders"] = ToSerializableDictionary(entry.RequestHeaders),
                ["RequestBodyHash"] = entry.RequestBodyHash,
                ["RequestBodyBase64"] = entry.RequestBodyBase64,
                ["StatusCode"] = entry.StatusCode,
                ["ResponseHeaders"] = ToSerializableDictionary(entry.ResponseHeaders),
                ["ResponseBodyBase64"] = entry.ResponseBodyBase64,
                ["Error"] = ToSerializableDictionary(entry.Error),
                ["ThrowsException"] = entry.ThrowsException,
                ["TimestampUtcTicks"] = entry.TimestampUtcTicks
            };
        }

        private static Dictionary<string, object> ToSerializableDictionary(Dictionary<string, List<string>> headers)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
                return result;

            foreach (var pair in headers)
            {
                var values = new List<object>();
                if (pair.Value != null)
                {
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        values.Add(pair.Value[i] ?? string.Empty);
                    }
                }
                result[pair.Key] = values;
            }

            return result;
        }

        private static Dictionary<string, object> ToSerializableDictionary(RecordingErrorDto error)
        {
            if (error == null)
                return null;

            return new Dictionary<string, object>
            {
                ["Type"] = error.Type,
                ["Message"] = error.Message,
                ["StatusCode"] = error.StatusCode.HasValue ? (object)error.StatusCode.Value : null
            };
        }

        private static RecordingFileDto FromSerializableObject(object value)
        {
            if (value is not Dictionary<string, object> dict)
            {
                throw new InvalidOperationException(
                    "Recording payload must deserialize into Dictionary<string, object>.");
            }

            var file = new RecordingFileDto
            {
                Version = ReadInt(dict, "Version"),
                CreatedUtcTicks = ReadLong(dict, "CreatedUtcTicks"),
                UpdatedUtcTicks = ReadLong(dict, "UpdatedUtcTicks"),
                Entries = new List<RecordingEntryDto>()
            };

            var entriesObject = ReadValue(dict, "Entries");
            if (entriesObject is List<object> entries)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    file.Entries.Add(FromSerializableEntry(entries[i]));
                }
            }

            return file;
        }

        private static RecordingEntryDto FromSerializableEntry(object value)
        {
            if (value is not Dictionary<string, object> dict)
            {
                throw new InvalidOperationException(
                    "Recording entry payload must be Dictionary<string, object>.");
            }

            return new RecordingEntryDto
            {
                Sequence = ReadLong(dict, "Sequence"),
                RequestKey = ReadString(dict, "RequestKey"),
                Method = ReadString(dict, "Method"),
                Url = ReadString(dict, "Url"),
                RequestHeaders = ParseHeaders(ReadValue(dict, "RequestHeaders")),
                RequestBodyHash = ReadString(dict, "RequestBodyHash"),
                RequestBodyBase64 = ReadString(dict, "RequestBodyBase64"),
                StatusCode = ReadInt(dict, "StatusCode"),
                ResponseHeaders = ParseHeaders(ReadValue(dict, "ResponseHeaders")),
                ResponseBodyBase64 = ReadString(dict, "ResponseBodyBase64"),
                Error = FromSerializableError(ReadValue(dict, "Error")),
                ThrowsException = ReadBool(dict, "ThrowsException"),
                TimestampUtcTicks = ReadLong(dict, "TimestampUtcTicks")
            };
        }

        private static RecordingErrorDto FromSerializableError(object value)
        {
            if (value is not Dictionary<string, object> dict)
                return null;

            return new RecordingErrorDto
            {
                Type = ReadString(dict, "Type"),
                Message = ReadString(dict, "Message"),
                StatusCode = TryReadNullableInt(dict, "StatusCode")
            };
        }

        private static Dictionary<string, List<string>> ParseHeaders(object value)
        {
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (value is not Dictionary<string, object> dict)
                return headers;

            foreach (var pair in dict)
            {
                if (pair.Value is List<object> objectList)
                {
                    var values = new List<string>(objectList.Count);
                    for (int i = 0; i < objectList.Count; i++)
                    {
                        values.Add(objectList[i]?.ToString() ?? string.Empty);
                    }
                    headers[pair.Key] = values;
                    continue;
                }

                if (pair.Value is IEnumerable enumerable && pair.Value is not string)
                {
                    var values = new List<string>();
                    foreach (var item in enumerable)
                    {
                        values.Add(item?.ToString() ?? string.Empty);
                    }
                    headers[pair.Key] = values;
                    continue;
                }

                headers[pair.Key] = new List<string> { pair.Value?.ToString() ?? string.Empty };
            }

            return headers;
        }

        private static object ReadValue(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value : null;
        }

        private static string ReadString(Dictionary<string, object> dict, string key)
        {
            return ReadValue(dict, key)?.ToString();
        }

        private static bool ReadBool(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return false;
            if (value is bool b)
                return b;
            if (bool.TryParse(value.ToString(), out var parsed))
                return parsed;
            return false;
        }

        private static int ReadInt(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return 0;
            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is ulong ul)
                return (int)ul;
            if (value is double d)
                return (int)d;
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        private static int? TryReadNullableInt(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return null;
            return ReadInt(dict, key);
        }

        private static long ReadLong(Dictionary<string, object> dict, string key)
        {
            var value = ReadValue(dict, key);
            if (value == null)
                return 0L;
            if (value is long l)
                return l;
            if (value is int i)
                return i;
            if (value is ulong ul)
                return (long)ul;
            if (value is double d)
                return (long)d;
            if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0L;
        }

        private static string SerializeJson(object value, Type type)
        {
            var serializerType = Type.GetType("TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON", throwOnError: false);
            if (serializerType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for RecordReplayTransport serialization.");
            }

            var serializeMethod = serializerType.GetMethod(
                "Serialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(object), typeof(Type) },
                modifiers: null);
            if (serializeMethod == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonSerializer.Serialize(object, Type) was not found.");
            }

            try
            {
                return (string)serializeMethod.Invoke(null, new[] { value, type });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException("RecordReplay serialization failed.", tie.InnerException);
            }
        }

        private static object DeserializeJson(string json, Type type)
        {
            var serializerType = Type.GetType("TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON", throwOnError: false);
            if (serializerType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for RecordReplayTransport deserialization.");
            }

            var deserializeMethod = serializerType.GetMethod(
                "Deserialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(Type) },
                modifiers: null);
            if (deserializeMethod == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON.JsonSerializer.Deserialize(string, Type) was not found.");
            }

            try
            {
                return deserializeMethod.Invoke(null, new object[] { json, type });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException("RecordReplay deserialization failed.", tie.InnerException);
            }
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
