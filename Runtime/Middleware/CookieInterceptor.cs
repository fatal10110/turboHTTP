using System;
using System.Collections.Generic;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Adds outbound Cookie headers and persists inbound Set-Cookie headers.
    /// </summary>
    public sealed class CookieInterceptor : IHttpInterceptor, IDisposable
    {
        private readonly CookieJar _jar;
        private readonly bool _ownsJar;
        private int _disposed;

        public CookieJar Jar => _jar;

        public CookieInterceptor(CookieJar jar = null)
        {
            if (jar == null)
            {
                _jar = new CookieJar();
                _ownsJar = true;
            }
            else
            {
                _jar = jar;
                _ownsJar = false;
            }
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                ThrowIfDisposed();

                var requestForNext = request;
                if (request.Uri.IsAbsoluteUri &&
                    (string.Equals(request.Uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(request.Uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                {
                    var isCrossSite = request.Metadata != null &&
                                      request.Metadata.TryGetValue(RequestMetadataKeys.IsCrossSiteRequest, out var crossSiteRaw) &&
                                      crossSiteRaw is bool crossSiteBool &&
                                      crossSiteBool;

                    var jarCookieHeader = _jar.GetCookieHeader(request.Uri, request.Method, isCrossSite);
                    if (!string.IsNullOrEmpty(jarCookieHeader))
                    {
                        requestForNext = request.Clone();
                        var existingCookieHeader = request.Headers.Get("Cookie");
                        requestForNext.WithHeader("Cookie", MergeCookieHeaders(existingCookieHeader, jarCookieHeader));
                        context.UpdateRequest(requestForNext);
                        context.RecordEvent("CookieAttached");
                    }
                }

                await next(
                    requestForNext,
                    new CookieHandler(handler, _jar, requestForNext.Uri),
                    context,
                    cancellationToken).ConfigureAwait(false);
            };
        }

        private static string MergeCookieHeaders(string existingCookieHeader, string jarCookieHeader)
        {
            if (string.IsNullOrWhiteSpace(existingCookieHeader))
                return jarCookieHeader;

            if (string.IsNullOrWhiteSpace(jarCookieHeader))
                return existingCookieHeader;

            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            var mergedTokens = new List<string>();

            AppendCookieHeaderTokens(existingCookieHeader, existingNames, mergedTokens, onlyAppendNewNames: false);
            AppendCookieHeaderTokens(jarCookieHeader, existingNames, mergedTokens, onlyAppendNewNames: true);

            if (mergedTokens.Count == 0)
                return null;

            return string.Join("; ", mergedTokens);
        }

        private static void AppendCookieHeaderTokens(
            string cookieHeader,
            HashSet<string> existingNames,
            List<string> mergedTokens,
            bool onlyAppendNewNames)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return;

            var tokens = cookieHeader.Split(';');
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].Trim();
                if (token.Length == 0)
                    continue;

                var cookieName = ExtractCookieName(token);
                if (onlyAppendNewNames && existingNames.Contains(cookieName))
                    continue;

                if (!onlyAppendNewNames)
                    existingNames.Add(cookieName);

                mergedTokens.Add(token);
            }
        }

        private static string ExtractCookieName(string cookieToken)
        {
            int equalsIndex = cookieToken.IndexOf('=');
            if (equalsIndex <= 0)
                return cookieToken.Trim();

            return cookieToken.Substring(0, equalsIndex).Trim();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_ownsJar)
                _jar.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(CookieInterceptor));
        }
    }
}
