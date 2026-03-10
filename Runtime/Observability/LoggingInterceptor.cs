using System;
using System.Collections.Generic;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Interceptor that logs HTTP requests and responses.
    /// Sensitive headers (Authorization, Cookie, etc.) are automatically redacted
    /// unless explicitly disabled via <see cref="redactSensitiveHeaders"/>.
    /// </summary>
    public sealed class LoggingInterceptor : IHttpInterceptor
    {
        private readonly Action<string> _log;
        private readonly LogLevel _logLevel;
        private readonly bool _logHeaders;
        private readonly bool _logBody;
        private readonly bool _redactSensitiveHeaders;

        private static readonly HashSet<string> DefaultSensitiveHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Authorization",
                "Cookie",
                "Set-Cookie",
                "Proxy-Authorization",
                "WWW-Authenticate",
                "X-Api-Key"
            };

        public enum LogLevel
        {
            None,
            Minimal,   // Only log URL and status
            Standard,  // Log URL, status, elapsed time
            Detailed   // Log everything including headers and body
        }

        public LoggingInterceptor(
            Action<string> log = null,
            LogLevel logLevel = LogLevel.Standard,
            bool logHeaders = false,
            bool logBody = false,
            bool redactSensitiveHeaders = true)
        {
            _log = log ?? (_ => { });
            _logLevel = logLevel;
            _logHeaders = logHeaders;
            _logBody = logBody;
            _redactSensitiveHeaders = redactSensitiveHeaders;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (_logLevel == LogLevel.None)
                    return next(request, handler, context, cancellationToken);

                LogRequest(request);

                return next(
                    request,
                    new LoggingHandler(
                        handler,
                        _log,
                        request,
                        _logLevel,
                        _logHeaders,
                        _logBody,
                        _redactSensitiveHeaders,
                        DefaultSensitiveHeaders,
                        context.Elapsed),
                    context,
                    cancellationToken);
            };
        }

        private void LogRequest(UHttpRequest request)
        {
            var messageBuilder = new StringBuilder();
            messageBuilder.Append("-> ").Append(request.Method).Append(' ').Append(request.Uri);

            if (_logLevel >= LogLevel.Detailed && _logHeaders && request.Headers.Count > 0)
            {
                messageBuilder.Append("\n  Headers:");
                foreach (var header in request.Headers)
                {
                    var value = ShouldRedact(header.Key) ? "****" : header.Value;
                    messageBuilder.Append("\n    ").Append(header.Key).Append(": ").Append(value);
                }
            }

            if (_logLevel >= LogLevel.Detailed && _logBody && !request.Body.IsEmpty)
            {
                int previewBytes = Math.Min(request.Body.Length, 500);
                var bodyPreview = System.Text.Encoding.UTF8.GetString(request.Body.Span.Slice(0, previewBytes));
                if (request.Body.Length > 500)
                    bodyPreview += "...";
                messageBuilder.Append("\n  Body: ").Append(bodyPreview);
            }

            _log($"[TurboHTTP] {messageBuilder}");
        }

        internal static string FormatResponseHeaders(
            HttpHeaders headers,
            bool redactSensitiveHeaders,
            HashSet<string> sensitiveHeaders)
        {
            if (headers == null || headers.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.Append("\n  Headers:");
            foreach (var header in headers)
            {
                var value = redactSensitiveHeaders && sensitiveHeaders.Contains(header.Key)
                    ? "****"
                    : header.Value;
                builder.Append("\n    ").Append(header.Key).Append(": ").Append(value);
            }

            return builder.ToString();
        }

        internal static string FormatResponseBodyPreview(byte[] buffer, int length, bool truncated)
        {
            if (buffer == null || length <= 0)
                return string.Empty;

            var bodyPreview = Encoding.UTF8.GetString(buffer, 0, length);
            return truncated ? bodyPreview + "..." : bodyPreview;
        }

        private bool ShouldRedact(string headerName)
        {
            return _redactSensitiveHeaders && DefaultSensitiveHeaders.Contains(headerName);
        }
    }
}
