using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Handles RFC-compliant redirect following for 301/302/303/307/308 responses.
    /// </summary>
    public sealed class RedirectInterceptor : IHttpInterceptor
    {
        private readonly bool _defaultFollowRedirects;
        private readonly int _defaultMaxRedirects;
        private readonly bool _defaultAllowHttpsToHttpDowngrade;
        private readonly bool _defaultEnforceRedirectTotalTimeout;

        public RedirectInterceptor(
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

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                if (!ResolveFollowRedirects(request.Metadata, _defaultFollowRedirects))
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var redirectHandler = new RedirectHandler(
                    handler,
                    next,
                    request,
                    ResolveOptions(request.Metadata),
                    context,
                    cancellationToken);

                await next(request, redirectHandler, context, cancellationToken).ConfigureAwait(false);
                await redirectHandler.Completion.ConfigureAwait(false);
            };
        }

        internal RedirectOptions ResolveOptions(IReadOnlyDictionary<string, object> metadata)
        {
            return new RedirectOptions(
                ResolveMaxRedirects(metadata, _defaultMaxRedirects),
                ResolveAllowHttpsToHttpDowngrade(metadata, _defaultAllowHttpsToHttpDowngrade),
                ResolveEnforceRedirectTotalTimeout(metadata, _defaultEnforceRedirectTotalTimeout));
        }

        internal static UHttpException CreateRedirectError(UHttpErrorType type, string message, Exception inner = null)
        {
            return new UHttpException(new UHttpError(type, message, inner));
        }

        internal static bool TryResolveRedirectTarget(
            Uri currentUri,
            int statusCode,
            HttpHeaders headers,
            out Uri targetUri)
        {
            targetUri = null;
            if (!IsRedirectStatus((HttpStatusCode)statusCode))
                return false;

            var location = headers.Get("Location");
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
                throw CreateRedirectError(
                    UHttpErrorType.InvalidRequest,
                    $"Invalid redirect Location header '{location}'.",
                    ex);
            }
        }

        internal static UHttpRequest BuildRedirectRequest(
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
                    body = ReadOnlyMemory<byte>.Empty;
                    RemoveBodyHeaders(headers);
                }
            }
            else if (statusCode == HttpStatusCode.SeeOther)
            {
                if (source.Method != HttpMethod.HEAD)
                {
                    method = HttpMethod.GET;
                    body = ReadOnlyMemory<byte>.Empty;
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
                CopyBodyForRedirect(body),
                source.Timeout,
                metadata);
        }

        internal static UHttpRequest ApplyTotalRedirectTimeoutBudget(
            UHttpRequest request,
            RequestContext context,
            TimeSpan totalTimeoutBudget)
        {
            var elapsed = context.Elapsed;
            var remaining = totalTimeoutBudget - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateRedirectError(
                    UHttpErrorType.Timeout,
                    $"Redirect chain exceeded total timeout budget ({totalTimeoutBudget.TotalSeconds:F0}s).");
            }

            if (remaining >= request.Timeout)
                return request;

            return new UHttpRequest(
                request.Method,
                request.Uri,
                request.Headers,
                CopyBodyForRedirect(request.Body),
                timeout: remaining,
                metadata: request.Metadata);
        }

        internal static bool IsCrossOrigin(Uri from, Uri to)
        {
            return !string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase)
                   || !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
                   || from.Port != to.Port;
        }

        internal static bool IsHttpsToHttpDowngrade(Uri from, Uri to)
        {
            return string.Equals(from.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(to.Scheme, "http", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildLoopKey(Uri targetUri)
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

        private static byte[] CopyBodyForRedirect(ReadOnlyMemory<byte> body)
        {
            if (body.IsEmpty)
                return null;

            if (MemoryMarshal.TryGetArray(body, out var segment)
                && segment.Array != null
                && segment.Offset == 0
                && segment.Count == segment.Array.Length)
            {
                return segment.Array;
            }

            return body.ToArray();
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

            return metadata.TryGetValue(RequestMetadataKeys.FollowRedirects, out var raw) && raw is bool boolValue
                ? boolValue
                : fallback;
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

            return metadata.TryGetValue(RequestMetadataKeys.AllowHttpsToHttpDowngrade, out var raw) && raw is bool boolValue
                ? boolValue
                : fallback;
        }

        private static bool ResolveEnforceRedirectTotalTimeout(IReadOnlyDictionary<string, object> metadata, bool fallback)
        {
            if (metadata == null)
                return fallback;

            return metadata.TryGetValue(RequestMetadataKeys.EnforceRedirectTotalTimeout, out var raw) && raw is bool boolValue
                ? boolValue
                : fallback;
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

        internal readonly struct RedirectOptions
        {
            internal RedirectOptions(
                int maxRedirects,
                bool allowHttpsToHttpDowngrade,
                bool enforceRedirectTotalTimeout)
            {
                MaxRedirects = maxRedirects;
                AllowHttpsToHttpDowngrade = allowHttpsToHttpDowngrade;
                EnforceRedirectTotalTimeout = enforceRedirectTotalTimeout;
            }

            internal int MaxRedirects { get; }
            internal bool AllowHttpsToHttpDowngrade { get; }
            internal bool EnforceRedirectTotalTimeout { get; }
        }
    }
}
