using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Distinguishes transport-level failures from HTTP status failures.
    /// </summary>
    public enum HttpMonitorFailureKind
    {
        None,
        HttpStatusError,
        TransportError
    }

    /// <summary>
    /// Immutable timeline snapshot used by <see cref="HttpMonitorEvent"/>.
    /// </summary>
    [Serializable]
    public sealed class HttpMonitorTimelineEvent
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyData =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

        public string Name { get; }
        public TimeSpan Timestamp { get; }
        public IReadOnlyDictionary<string, string> Data { get; }

        public HttpMonitorTimelineEvent(
            string name,
            TimeSpan timestamp,
            IReadOnlyDictionary<string, string> data = null)
        {
            Name = name ?? string.Empty;
            Timestamp = timestamp;
            Data = CopyData(data);
        }

        private static IReadOnlyDictionary<string, string> CopyData(
            IReadOnlyDictionary<string, string> data)
        {
            if (data == null || data.Count == 0)
            {
                return EmptyData;
            }

            var copy = new Dictionary<string, string>(data.Count, StringComparer.Ordinal);
            foreach (var pair in data)
            {
                copy[pair.Key ?? string.Empty] = pair.Value ?? string.Empty;
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }
    }

    /// <summary>
    /// Immutable request/response capture record used by the HTTP monitor tooling.
    /// </summary>
    [Serializable]
    public sealed class HttpMonitorEvent
    {
        private static readonly UTF8Encoding Utf8NoThrow = new UTF8Encoding(false, false);
        private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        private static readonly IReadOnlyList<HttpMonitorTimelineEvent> EmptyTimeline =
            Array.Empty<HttpMonitorTimelineEvent>();

        public string Id { get; }
        public DateTime Timestamp { get; }

        public string Method { get; }
        public string Url { get; }
        public IReadOnlyDictionary<string, string> RequestHeaders { get; }
        public ReadOnlyMemory<byte> RequestBody { get; }
        public int OriginalRequestBodySize { get; }
        public bool IsRequestBodyTruncated { get; }
        public bool IsRequestBodyBinary { get; }

        public int StatusCode { get; }
        public string StatusText { get; }
        public IReadOnlyDictionary<string, string> ResponseHeaders { get; }
        public ReadOnlyMemory<byte> ResponseBody { get; }
        public int OriginalResponseBodySize { get; }
        public bool IsResponseBodyTruncated { get; }
        public bool IsResponseBodyBinary { get; }

        public TimeSpan ElapsedTime { get; }
        public IReadOnlyList<HttpMonitorTimelineEvent> Timeline { get; }

        public string Error { get; }
        public UHttpErrorType? ErrorType { get; }
        public HttpMonitorFailureKind FailureKind { get; }

        public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode < 300;
        public bool IsTransportFailure => FailureKind == HttpMonitorFailureKind.TransportError;
        public bool IsHttpStatusError => FailureKind == HttpMonitorFailureKind.HttpStatusError;
        public bool IsError => FailureKind != HttpMonitorFailureKind.None;

        internal HttpMonitorEvent(
            string id,
            DateTime timestamp,
            string method,
            string url,
            IReadOnlyDictionary<string, string> requestHeaders,
            ReadOnlyMemory<byte> requestBody,
            int originalRequestBodySize,
            bool isRequestBodyTruncated,
            bool isRequestBodyBinary,
            int statusCode,
            string statusText,
            IReadOnlyDictionary<string, string> responseHeaders,
            ReadOnlyMemory<byte> responseBody,
            int originalResponseBodySize,
            bool isResponseBodyTruncated,
            bool isResponseBodyBinary,
            TimeSpan elapsedTime,
            IReadOnlyList<HttpMonitorTimelineEvent> timeline,
            string error,
            UHttpErrorType? errorType,
            HttpMonitorFailureKind failureKind)
        {
            Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            Timestamp = timestamp;

            Method = method ?? string.Empty;
            Url = url ?? string.Empty;
            RequestHeaders = CopyHeaders(requestHeaders);
            RequestBody = CloneBody(requestBody);
            OriginalRequestBodySize = Math.Max(0, originalRequestBodySize);
            IsRequestBodyTruncated = isRequestBodyTruncated;
            IsRequestBodyBinary = isRequestBodyBinary;

            StatusCode = statusCode;
            StatusText = statusText ?? string.Empty;
            ResponseHeaders = CopyHeaders(responseHeaders);
            ResponseBody = CloneBody(responseBody);
            OriginalResponseBodySize = Math.Max(0, originalResponseBodySize);
            IsResponseBodyTruncated = isResponseBodyTruncated;
            IsResponseBodyBinary = isResponseBodyBinary;

            ElapsedTime = elapsedTime;
            Timeline = CopyTimeline(timeline);

            Error = error ?? string.Empty;
            ErrorType = errorType;
            FailureKind = failureKind;
        }

        public string GetRequestBodyAsString()
        {
            return GetBodyAsString(
                RequestBody,
                RequestHeaders,
                IsRequestBodyBinary,
                IsRequestBodyTruncated,
                OriginalRequestBodySize);
        }

        public string GetResponseBodyAsString()
        {
            return GetBodyAsString(
                ResponseBody,
                ResponseHeaders,
                IsResponseBodyBinary,
                IsResponseBodyTruncated,
                OriginalResponseBodySize);
        }

        internal static bool IsLikelyBinaryPayload(
            ReadOnlyMemory<byte> body,
            IReadOnlyDictionary<string, string> headers)
        {
            if (body.IsEmpty)
            {
                return false;
            }

            if (HasLikelyBinaryContentType(headers))
            {
                return true;
            }

            var sampleLength = Math.Min(body.Length, 512);
            var span = body.Span;
            for (int i = 0; i < sampleLength; i++)
            {
                if (span[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetBodyAsString(
            ReadOnlyMemory<byte> body,
            IReadOnlyDictionary<string, string> headers,
            bool isBinary,
            bool isTruncated,
            int originalSize)
        {
            if (body.IsEmpty)
            {
                return string.Empty;
            }

            var totalSize = originalSize > 0 ? originalSize : body.Length;
            if (isBinary || IsLikelyBinaryPayload(body, headers))
            {
                var suffix = isTruncated ? ", preview only" : string.Empty;
                return $"<Binary Data: {totalSize} bytes{suffix}>";
            }

            var text = Utf8NoThrow.GetString(body.Span);
            if (isTruncated)
            {
                text += $"\n\n<Truncated: showing {body.Length}/{totalSize} bytes>";
            }

            return text;
        }

        private static bool HasLikelyBinaryContentType(
            IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null
                || !headers.TryGetValue("Content-Type", out var contentType)
                || string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

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

        private static ReadOnlyMemory<byte> CloneBody(ReadOnlyMemory<byte> body)
        {
            if (body.IsEmpty)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            return new ReadOnlyMemory<byte>(body.ToArray());
        }

        private static IReadOnlyDictionary<string, string> CopyHeaders(
            IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                return EmptyHeaders;
            }

            var copy = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in headers)
            {
                copy[pair.Key ?? string.Empty] = pair.Value ?? string.Empty;
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }

        private static IReadOnlyList<HttpMonitorTimelineEvent> CopyTimeline(
            IReadOnlyList<HttpMonitorTimelineEvent> timeline)
        {
            if (timeline == null || timeline.Count == 0)
            {
                return EmptyTimeline;
            }

            var copy = new HttpMonitorTimelineEvent[timeline.Count];
            for (int i = 0; i < timeline.Count; i++)
            {
                copy[i] = timeline[i] ?? new HttpMonitorTimelineEvent(string.Empty, TimeSpan.Zero);
            }

            return Array.AsReadOnly(copy);
        }
    }
}
