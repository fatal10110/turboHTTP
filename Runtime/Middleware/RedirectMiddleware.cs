using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly bool _defaultAllowHttpsToHttpDowngrade;
        private readonly bool _defaultEnforceRedirectTotalTimeout;

        public RedirectMiddleware(
            bool defaultFollowRedirects = true,
            int defaultMaxRedirects = 10,
            bool defaultAllowHttpsToHttpDowngrade = false,
            bool defaultEnforceRedirectTotalTimeout = true)
        {
            if (defaultMaxRedirects < 0)
                throw new ArgumentOutOfRangeException(nameof(defaultMaxRedirects), defaultMaxRedirects, "Must be >= 0.");

            _defaultFollowRedirects = defaultFollowRedirects;
            _defaultMaxRedirects = defaultMaxRedirects;
            _defaultAllowHttpsToHttpDowngrade = defaultAllowHttpsToHttpDowngrade;
            _defaultEnforceRedirectTotalTimeout = defaultEnforceRedirectTotalTimeout;
        }

        public async ValueTask<UHttpResponse> InvokeAsync(
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
            var allowHttpsToHttpDowngrade = ResolveAllowHttpsToHttpDowngrade(request.Metadata, _defaultAllowHttpsToHttpDowngrade);
            var enforceRedirectTotalTimeout = ResolveEnforceRedirectTotalTimeout(request.Metadata, _defaultEnforceRedirectTotalTimeout);
            var totalTimeoutBudget = request.Timeout;

            // Design decision: all redirect hops intentionally share one RequestContext so
            // telemetry/timing represent a single logical request chain.
            var currentRequest = request;
            var visitedTargets = new HashSet<string>(StringComparer.Ordinal);
            var redirectChain = new List<string> { request.Uri.AbsoluteUri };

            for (int redirectCount = 0; ; redirectCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (enforceRedirectTotalTimeout)
                {
                    currentRequest = ApplyTotalRedirectTimeoutBudget(currentRequest, context, totalTimeoutBudget);
                    context.UpdateRequest(currentRequest);
                }

                var response = await next(currentRequest, context, cancellationToken).ConfigureAwait(false);
                if (!TryResolveRedirectTarget(currentRequest.Uri, response, out var targetUri, out var statusCode))
                    return response;

                using (response)
                {
                    if (redirectCount >= maxRedirects)
                    {
                        throw new UHttpException(new UHttpError(
                            UHttpErrorType.InvalidRequest,
                            $"Redirect limit exceeded ({maxRedirects}). Last redirect target: {targetUri}"));
                    }

                    if (IsHttpsToHttpDowngrade(currentRequest.Uri, targetUri) && !allowHttpsToHttpDowngrade)
                    {
                        throw new UHttpException(new UHttpError(
                            UHttpErrorType.InvalidRequest,
                            $"Blocked insecure redirect downgrade from '{currentRequest.Uri}' to '{targetUri}'."));
                    }

                    var loopKey = BuildLoopKey(targetUri);
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
        }

        private static UHttpRequest BuildRedirectRequest(
            UHttpRequest source,
            Uri targetUri,
            HttpStatusCode statusCode,
            bool crossOrigin)
        {
            var method = source.Method;
            var body = source.Body;
            // Current API clones in UHttpRequest ctor as well; this extra clone is acceptable
            // for now and can be trimmed later via the ownsHeaders constructor path.
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
                var trimmedLocation = location.Trim();
                targetUri = new Uri(currentUri, trimmedLocation);
                targetUri = ApplyFragmentInheritance(currentUri, targetUri, trimmedLocation);
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
            // 300 Multiple Choices is intentionally not auto-followed in middleware.
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

        private static bool ResolveAllowHttpsToHttpDowngrade(IReadOnlyDictionary<string, object> metadata, bool fallback)
        {
            if (metadata == null)
                return fallback;

            if (!metadata.TryGetValue(RequestMetadataKeys.AllowHttpsToHttpDowngrade, out var raw))
                return fallback;

            if (raw is bool boolValue)
                return boolValue;

            return fallback;
        }

        private static bool ResolveEnforceRedirectTotalTimeout(IReadOnlyDictionary<string, object> metadata, bool fallback)
        {
            if (metadata == null)
                return fallback;

            if (!metadata.TryGetValue(RequestMetadataKeys.EnforceRedirectTotalTimeout, out var raw))
                return fallback;

            if (raw is bool boolValue)
                return boolValue;

            return fallback;
        }

        private static bool IsCrossOrigin(Uri from, Uri to)
        {
            return !string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)
                   || !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
                   || from.Port != to.Port;
        }

        private static bool IsHttpsToHttpDowngrade(Uri from, Uri to)
        {
            return string.Equals(from.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(to.Scheme, "http", StringComparison.OrdinalIgnoreCase);
        }

        private static UHttpRequest ApplyTotalRedirectTimeoutBudget(
            UHttpRequest request,
            RequestContext context,
            TimeSpan totalTimeoutBudget)
        {
            var elapsed = context.Elapsed;
            var remaining = totalTimeoutBudget - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    $"Redirect chain exceeded total timeout budget ({totalTimeoutBudget.TotalSeconds:F0}s)."));
            }

            if (remaining >= request.Timeout)
                return request;

            return new UHttpRequest(
                request.Method,
                request.Uri,
                request.Headers,
                request.Body,
                timeout: remaining,
                metadata: request.Metadata);
        }

        private static string BuildLoopKey(Uri targetUri)
        {
            var scheme = targetUri.Scheme.ToLowerInvariant();
            var host = targetUri.Host.ToLowerInvariant();
            var port = targetUri.IsDefaultPort
                ? string.Empty
                : ":" + targetUri.Port.ToString(CultureInfo.InvariantCulture);
            var pathAndQuery = targetUri.PathAndQuery;
            if (string.IsNullOrEmpty(pathAndQuery))
                pathAndQuery = "/";

            return scheme + "://" + host + port + pathAndQuery;
        }

        private static Uri ApplyFragmentInheritance(Uri currentUri, Uri targetUri, string rawLocation)
        {
            if (currentUri == null || targetUri == null || string.IsNullOrEmpty(rawLocation))
                return targetUri;

            if (rawLocation.IndexOf('#') >= 0)
                return targetUri;

            if (string.IsNullOrEmpty(currentUri.Fragment) || !string.IsNullOrEmpty(targetUri.Fragment))
                return targetUri;

            var builder = new UriBuilder(targetUri)
            {
                Fragment = currentUri.Fragment.Substring(1)
            };

            return builder.Uri;
        }
    }
}
