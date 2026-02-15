using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Middleware that logs HTTP requests and responses.
    /// Sensitive headers (Authorization, Cookie, etc.) are automatically redacted
    /// unless explicitly disabled via <see cref="redactSensitiveHeaders"/>.
    /// </summary>
    public class LoggingMiddleware : IHttpMiddleware
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

        public LoggingMiddleware(
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

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (_logLevel == LogLevel.None)
            {
                return await next(request, context, cancellationToken);
            }

            LogRequest(request);

            var startTime = DateTime.UtcNow;
            UHttpResponse response = null;
            Exception exception = null;

            try
            {
                response = await next(request, context, cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                var elapsed = DateTime.UtcNow - startTime;

                if (exception != null)
                {
                    LogError(request, exception, elapsed);
                }
                else if (response != null)
                {
                    LogResponse(request, response);
                }
            }
        }

        private void LogRequest(UHttpRequest request)
        {
            var message = $"-> {request.Method} {request.Uri}";

            if (_logLevel >= LogLevel.Detailed && _logHeaders && request.Headers.Count > 0)
            {
                message += "\n  Headers:";
                foreach (var header in request.Headers)
                {
                    var value = ShouldRedact(header.Key) ? "****" : header.Value;
                    message += $"\n    {header.Key}: {value}";
                }
            }

            if (_logLevel >= LogLevel.Detailed && _logBody && request.Body != null && request.Body.Length > 0)
            {
                int previewBytes = Math.Min(request.Body.Length, 500);
                var bodyPreview = System.Text.Encoding.UTF8.GetString(request.Body, 0, previewBytes);
                if (request.Body.Length > 500)
                    bodyPreview += "...";
                message += $"\n  Body: {bodyPreview}";
            }

            _log($"[TurboHTTP] {message}");
        }

        private void LogResponse(UHttpRequest request, UHttpResponse response)
        {
            var message = $"<- {request.Method} {request.Uri} -> {(int)response.StatusCode} {response.StatusCode} ({response.ElapsedTime.TotalMilliseconds:F0}ms)";

            if (_logLevel >= LogLevel.Detailed && _logHeaders && response.Headers.Count > 0)
            {
                message += "\n  Headers:";
                foreach (var header in response.Headers)
                {
                    var value = ShouldRedact(header.Key) ? "****" : header.Value;
                    message += $"\n    {header.Key}: {value}";
                }
            }

            if (_logLevel >= LogLevel.Detailed && _logBody && response.Body != null && response.Body.Length > 0)
            {
                int previewBytes = Math.Min(response.Body.Length, 500);
                var bodyPreview = System.Text.Encoding.UTF8.GetString(response.Body, 0, previewBytes);
                if (response.Body.Length > 500)
                    bodyPreview += "...";
                message += $"\n  Body: {bodyPreview}";
            }

            if (response.IsSuccessStatusCode)
            {
                _log($"[TurboHTTP] {message}");
            }
            else
            {
                _log($"[TurboHTTP][WARN] {message}");
            }
        }

        private void LogError(UHttpRequest request, Exception exception, TimeSpan elapsed)
        {
            var message = $"X {request.Method} {request.Uri} -> ERROR ({elapsed.TotalMilliseconds:F0}ms)\n  {exception.Message}";
            _log($"[TurboHTTP][ERROR] {message}");
        }

        private bool ShouldRedact(string headerName)
        {
            return _redactSensitiveHeaders && DefaultSensitiveHeaders.Contains(headerName);
        }
    }
}
