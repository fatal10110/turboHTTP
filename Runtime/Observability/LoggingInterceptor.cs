using System;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Interceptor that logs HTTP requests and responses.
    /// Redaction is on by default at every log level: URL query values and user-info, and the
    /// values of the sensitive headers of the <see cref="RedactionPolicy"/> (Authorization, Cookie,
    /// API-key and token headers, ...) are replaced by <see cref="RedactionPolicy.RedactedValue"/>.
    /// </summary>
    public sealed class LoggingInterceptor : IHttpInterceptor
    {
        private readonly Action<string> _log;
        private readonly LogLevel _logLevel;
        private readonly bool _logHeaders;
        private readonly bool _logBody;
        private readonly bool _redactSensitiveHeaders;

        private readonly RedactionPolicy _redactionPolicy;

        public enum LogLevel
        {
            None,
            Minimal,   // Only log URL and status
            Standard,  // Log URL, status, elapsed time
            Detailed   // Log everything including headers and body
        }

        /// <summary>
        /// Creates a logging interceptor.
        /// </summary>
        /// <param name="log">Log sink; <c>null</c> discards messages.</param>
        /// <param name="logLevel">Verbosity.</param>
        /// <param name="logHeaders">Log headers at <see cref="LogLevel.Detailed"/>.</param>
        /// <param name="logBody">Log a body preview at <see cref="LogLevel.Detailed"/> (bodies are not redacted).</param>
        /// <param name="redactSensitiveHeaders">
        /// Redact the values of the policy's sensitive headers (default <c>true</c>). URL redaction is
        /// controlled by <see cref="RedactionPolicy.RedactQueryValues"/>.
        /// </param>
        /// <param name="redactionPolicy">
        /// Shared redaction policy; <c>null</c> uses <see cref="RedactionPolicy.Default"/>. Add or remove
        /// sensitive headers with <see cref="RedactionPolicy.WithSensitiveHeaders"/> /
        /// <see cref="RedactionPolicy.WithoutSensitiveHeaders"/>; keep raw URLs with
        /// <see cref="RedactionPolicy.WithQueryValueRedaction"/>(false).
        /// </param>
        public LoggingInterceptor(
            Action<string> log = null,
            LogLevel logLevel = LogLevel.Standard,
            bool logHeaders = false,
            bool logBody = false,
            bool redactSensitiveHeaders = true,
            RedactionPolicy redactionPolicy = null)
        {
            _log = log ?? (_ => { });
            _logLevel = logLevel;
            _logHeaders = logHeaders;
            _logBody = logBody;
            _redactSensitiveHeaders = redactSensitiveHeaders;
            _redactionPolicy = redactionPolicy ?? RedactionPolicy.Default;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (_logLevel == LogLevel.None)
                    return next(request, handler, context, cancellationToken);

                var displayUrl = _redactionPolicy.RedactUrl(request.Uri);
                LogRequest(request, displayUrl);

                return next(
                    request,
                    new LoggingHandler(
                        handler,
                        _log,
                        request,
                        displayUrl,
                        _logLevel,
                        _logHeaders,
                        _logBody,
                        _redactSensitiveHeaders,
                        _redactionPolicy,
                        context.Elapsed),
                    context,
                    cancellationToken);
            };
        }

        private void LogRequest(UHttpRequest request, string displayUrl)
        {
            var messageBuilder = new StringBuilder();
            messageBuilder.Append("-> ").Append(request.Method).Append(' ').Append(displayUrl);

            if (_logLevel >= LogLevel.Detailed && _logHeaders && request.Headers.Count > 0)
            {
                messageBuilder.Append("\n  Headers:");
                foreach (var header in request.Headers)
                {
                    var value = ShouldRedact(header.Key) ? RedactionPolicy.RedactedValue : header.Value;
                    messageBuilder.Append("\n    ").Append(header.Key).Append(": ").Append(value);
                }
            }

            if (_logLevel >= LogLevel.Detailed
                && _logBody)
            {
                if (request.TryGetBufferedContent(out var body) && !body.IsEmpty)
                {
                    int previewBytes = Math.Min(body.Length, 500);
                    var bodyPreview = System.Text.Encoding.UTF8.GetString(body.Span.Slice(0, previewBytes));
                    if (body.Length > 500)
                        bodyPreview += "...";
                    messageBuilder.Append("\n  Body: ").Append(bodyPreview);
                }
                else if (request.Content != null && !request.Content.IsEmpty)
                {
                    var length = request.Content.Length.HasValue
                        ? request.Content.Length.Value.ToString()
                        : "unknown";
                    messageBuilder
                        .Append("\n  Body: <streaming body preview unavailable without buffering; length=")
                        .Append(length)
                        .Append("; replayability=")
                        .Append(request.Content.Replayability)
                        .Append('>');
                }
            }

            _log($"[TurboHTTP] {messageBuilder}");
        }

        internal static string FormatResponseHeaders(
            HttpHeaders headers,
            bool redactSensitiveHeaders,
            RedactionPolicy redactionPolicy)
        {
            if (headers == null || headers.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.Append("\n  Headers:");
            foreach (var header in headers)
            {
                var value = redactSensitiveHeaders && redactionPolicy.IsSensitiveHeader(header.Key)
                    ? RedactionPolicy.RedactedValue
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
            return _redactSensitiveHeaders && _redactionPolicy.IsSensitiveHeader(headerName);
        }
    }
}
