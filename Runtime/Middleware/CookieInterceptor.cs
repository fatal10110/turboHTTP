using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

                Task dispatchTask;
                try
                {
                    dispatchTask = next(
                        requestForNext,
                        new CookieHandler(handler, _jar, requestForNext.Uri),
                        context,
                        cancellationToken);
                }
                catch
                {
                    if (!ReferenceEquals(requestForNext, request))
                    {
                        context.UpdateRequest(request);
                        requestForNext.Dispose();
                    }

                    throw;
                }

                await dispatchTask.ConfigureAwait(false);
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

            int start = 0;
            while (start < cookieHeader.Length)
            {
                int end = cookieHeader.IndexOf(';', start);
                if (end < 0)
                    end = cookieHeader.Length;

                int tokenStart = start;
                int tokenEnd = end;
                while (tokenStart < tokenEnd && char.IsWhiteSpace(cookieHeader[tokenStart]))
                    tokenStart++;
                while (tokenEnd > tokenStart && char.IsWhiteSpace(cookieHeader[tokenEnd - 1]))
                    tokenEnd--;

                if (tokenEnd > tokenStart)
                {
                    var cookieName = ExtractCookieName(cookieHeader, tokenStart, tokenEnd);
                    if (!onlyAppendNewNames || !existingNames.Contains(cookieName))
                    {
                        if (!onlyAppendNewNames)
                            existingNames.Add(cookieName);

                        mergedTokens.Add(cookieHeader.Substring(tokenStart, tokenEnd - tokenStart));
                    }
                }

                start = end + 1;
            }
        }

        private static string ExtractCookieName(string cookieHeader, int start, int end)
        {
            int equalsIndex = -1;
            for (int i = start; i < end; i++)
            {
                if (cookieHeader[i] == '=')
                {
                    equalsIndex = i;
                    break;
                }
            }

            if (equalsIndex <= 0)
                return cookieHeader.Substring(start, end - start);

            int nameEnd = equalsIndex;
            while (nameEnd > start && char.IsWhiteSpace(cookieHeader[nameEnd - 1]))
                nameEnd--;

            return cookieHeader.Substring(start, nameEnd - start);
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
