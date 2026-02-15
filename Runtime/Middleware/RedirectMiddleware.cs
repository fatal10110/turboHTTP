using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Handles RFC-compliant redirect following for 301/302/303/307/308 responses.
    /// </summary>
    public sealed class RedirectMiddleware : IHttpMiddleware
    {
        private readonly bool _defaultFollowRedirects;
        private readonly int _defaultMaxRedirects;

        public RedirectMiddleware(bool defaultFollowRedirects = true, int defaultMaxRedirects = 10)
        {
            if (defaultMaxRedirects < 0)
                throw new ArgumentOutOfRangeException(nameof(defaultMaxRedirects), defaultMaxRedirects, "Must be >= 0.");

            _defaultFollowRedirects = defaultFollowRedirects;
            _defaultMaxRedirects = defaultMaxRedirects;
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            var followRedirects = ResolveFollowRedirects(request.Metadata, _defaultFollowRedirects);
            if (!followRedirects)
                return await next(request, context, cancellationToken).ConfigureAwait(false);

            var maxRedirects = ResolveMaxRedirects(request.Metadata, _defaultMaxRedirects);
            var currentRequest = request;
            var visitedTargets = new HashSet<string>(StringComparer.Ordinal);
            var redirectChain = new List<string> { request.Uri.AbsoluteUri };

            for (int redirectCount = 0; ; redirectCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await next(currentRequest, context, cancellationToken).ConfigureAwait(false);
                if (!TryResolveRedirectTarget(currentRequest.Uri, response, out var targetUri, out var statusCode))
                    return response;

                if (redirectCount >= maxRedirects)
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.InvalidRequest,
                        $"Redirect limit exceeded ({maxRedirects}). Last redirect target: {targetUri}"));
                }

                var loopKey = BuildLoopKey(targetUri, statusCode);
                if (!visitedTargets.Add(loopKey))
                {
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.InvalidRequest,
                        $"Redirect loop detected for target '{targetUri}'."));
                }

                var fromUri = currentRequest.Uri;
                var crossOrigin = IsCrossOrigin(currentRequest.Uri, targetUri);
                currentRequest = BuildRedirectRequest(currentRequest, targetUri, statusCode, crossOrigin);

                redirectChain.Add(targetUri.AbsoluteUri);
                context.SetState("RedirectChain", redirectChain.ToArray());
                context.RecordEvent("RedirectHop", new Dictionary<string, object>
                {
                    { "from", fromUri.AbsoluteUri },
                    { "to", targetUri.AbsoluteUri },
                    { "status", (int)statusCode },
                    { "hop", redirectCount + 1 }
                });

                context.UpdateRequest(currentRequest);
            }
        }

        private static UHttpRequest BuildRedirectRequest(
            UHttpRequest source,
            Uri targetUri,
            HttpStatusCode statusCode,
            bool crossOrigin)
        {
            var method = source.Method;
            var body = source.Body;
            var headers = source.Headers.Clone();

            headers.Remove("Host");

            if (statusCode == HttpStatusCode.MovedPermanently || statusCode == HttpStatusCode.Found)
            {
                if (source.Method == HttpMethod.POST)
                {
                    method = HttpMethod.GET;
                    body = null;
                    RemoveBodyHeaders(headers);
                }
            }
            else if (statusCode == HttpStatusCode.SeeOther)
            {
                if (source.Method != HttpMethod.HEAD)
                {
                    method = HttpMethod.GET;
                    body = null;
                    RemoveBodyHeaders(headers);
                }
            }

            if (crossOrigin)
            {
                headers.Remove("Authorization");
                headers.Remove("Proxy-Authorization");
                headers.Remove("Cookie");
            }

            var metadata = CloneMetadata(source.Metadata);
            metadata[RequestMetadataKeys.IsCrossSiteRequest] = crossOrigin;

            return new UHttpRequest(
                method,
                targetUri,
                headers,
                body,
                source.Timeout,
                metadata);
        }

        private static void RemoveBodyHeaders(HttpHeaders headers)
        {
            headers.Remove("Content-Length");
            headers.Remove("Content-Type");
            headers.Remove("Transfer-Encoding");
        }

        private static Dictionary<string, object> CloneMetadata(IReadOnlyDictionary<string, object> metadata)
        {
            if (metadata == null || metadata.Count == 0)
                return new Dictionary<string, object>();

            return new Dictionary<string, object>(metadata);
        }

        private static bool TryResolveRedirectTarget(
            Uri currentUri,
            UHttpResponse response,
            out Uri targetUri,
            out HttpStatusCode statusCode)
        {
            targetUri = null;
            statusCode = response.StatusCode;

            if (!IsRedirectStatus(statusCode))
                return false;

            var location = response.Headers.Get("Location");
            if (string.IsNullOrWhiteSpace(location))
                return false;

            try
            {
                targetUri = new Uri(currentUri, location.Trim());
                return true;
            }
            catch (UriFormatException ex)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.InvalidRequest,
                    $"Invalid redirect Location header '{location}'.",
                    ex));
            }
        }

        private static bool IsRedirectStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.MovedPermanently
                   || statusCode == HttpStatusCode.Found
                   || statusCode == HttpStatusCode.SeeOther
                   || statusCode == HttpStatusCode.TemporaryRedirect
                   || (int)statusCode == 308;
        }

        private static bool ResolveFollowRedirects(IReadOnlyDictionary<string, object> metadata, bool fallback)
        {
            if (metadata == null)
                return fallback;

            if (!metadata.TryGetValue(RequestMetadataKeys.FollowRedirects, out var raw))
                return fallback;

            if (raw is bool boolValue)
                return boolValue;

            return fallback;
        }

        private static int ResolveMaxRedirects(IReadOnlyDictionary<string, object> metadata, int fallback)
        {
            if (metadata == null)
                return fallback;

            if (!metadata.TryGetValue(RequestMetadataKeys.MaxRedirects, out var raw))
                return fallback;

            if (raw is int intValue && intValue >= 0)
                return intValue;

            if (raw is long longValue && longValue >= 0 && longValue <= int.MaxValue)
                return (int)longValue;

            return fallback;
        }

        private static bool IsCrossOrigin(Uri from, Uri to)
        {
            return !string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)
                   || !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
                   || from.Port != to.Port;
        }

        private static string BuildLoopKey(Uri targetUri, HttpStatusCode statusCode)
        {
            return ((int)statusCode).ToString() + " " + targetUri.AbsoluteUri;
        }
    }
}
