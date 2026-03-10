using System;
using System.Collections.Generic;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    internal sealed class LoggingHandler : IHttpHandler
    {
        private const int MaxPreviewBytes = 500;

        private readonly IHttpHandler _inner;
        private readonly Action<string> _log;
        private readonly UHttpRequest _request;
        private readonly LoggingInterceptor.LogLevel _logLevel;
        private readonly bool _logHeaders;
        private readonly bool _logBody;
        private readonly bool _redactSensitiveHeaders;
        private readonly HashSet<string> _sensitiveHeaders;
        private readonly TimeSpan _started;

        private byte[] _bodyPreview;
        private int _bodyPreviewLength;
        private bool _bodyPreviewTruncated;
        private long _bytesReceived;
        private int _statusCode;
        private string _statusText;

        internal LoggingHandler(
            IHttpHandler inner,
            Action<string> log,
            UHttpRequest request,
            LoggingInterceptor.LogLevel logLevel,
            bool logHeaders,
            bool logBody,
            bool redactSensitiveHeaders,
            HashSet<string> sensitiveHeaders,
            TimeSpan started)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? (_ => { });
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _logLevel = logLevel;
            _logHeaders = logHeaders;
            _logBody = logBody;
            _redactSensitiveHeaders = redactSensitiveHeaders;
            _sensitiveHeaders = sensitiveHeaders ?? throw new ArgumentNullException(nameof(sensitiveHeaders));
            _started = started;
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _statusCode = statusCode;
            _statusText = statusCode.ToString();

            var builder = new StringBuilder();
            builder.Append("<- ")
                .Append(_request.Method)
                .Append(' ')
                .Append(_request.Uri)
                .Append(" -> ")
                .Append(statusCode);

            if (_logLevel >= LoggingInterceptor.LogLevel.Standard)
                builder.Append(' ').Append(_statusText);

            if (_logLevel >= LoggingInterceptor.LogLevel.Detailed && _logHeaders)
            {
                builder.Append(LoggingInterceptor.FormatResponseHeaders(
                    headers,
                    _redactSensitiveHeaders,
                    _sensitiveHeaders));
            }

            _log(GetLogPrefix() + builder);
            _inner.OnResponseStart(statusCode, headers, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            _bytesReceived += chunk.Length;

            if (_logLevel >= LoggingInterceptor.LogLevel.Detailed && _logBody && !chunk.IsEmpty)
            {
                if (_bodyPreview == null)
                    _bodyPreview = new byte[MaxPreviewBytes];

                var remaining = MaxPreviewBytes - _bodyPreviewLength;
                if (remaining > 0)
                {
                    var copyLength = Math.Min(remaining, chunk.Length);
                    chunk.Slice(0, copyLength).CopyTo(_bodyPreview.AsSpan(_bodyPreviewLength, copyLength));
                    _bodyPreviewLength += copyLength;
                    _bodyPreviewTruncated |= copyLength < chunk.Length;
                }
                else
                {
                    _bodyPreviewTruncated = true;
                }
            }

            _inner.OnResponseData(chunk, context);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            var elapsed = context.Elapsed - _started;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            var builder = new StringBuilder();
            builder.Append("<= ")
                .Append(_request.Method)
                .Append(' ')
                .Append(_request.Uri)
                .Append(" completed in ")
                .Append(elapsed.TotalMilliseconds.ToString("F0"))
                .Append("ms")
                .Append(" (")
                .Append(_bytesReceived)
                .Append(" bytes)");

            if (_logLevel >= LoggingInterceptor.LogLevel.Detailed && _logBody && _bodyPreviewLength > 0)
            {
                builder.Append("\n  Body: ")
                    .Append(LoggingInterceptor.FormatResponseBodyPreview(
                        _bodyPreview ?? Array.Empty<byte>(),
                        _bodyPreviewLength,
                        _bodyPreviewTruncated));
            }

            _log(GetLogPrefix() + builder);
            _inner.OnResponseEnd(trailers, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            var elapsed = context.Elapsed - _started;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            var builder = new StringBuilder();
            builder.Append("X ")
                .Append(_request.Method)
                .Append(' ')
                .Append(_request.Uri)
                .Append(" -> ERROR (")
                .Append(elapsed.TotalMilliseconds.ToString("F0"))
                .Append("ms)\n  ")
                .Append(error?.Message ?? "Unknown error");

            _log("[TurboHTTP][ERROR] " + builder);
            _inner.OnResponseError(error, context);
        }

        private string GetLogPrefix()
        {
            if (_statusCode == 0 || (_statusCode >= 200 && _statusCode < 300))
                return "[TurboHTTP] ";

            return "[TurboHTTP][WARN] ";
        }
    }
}
