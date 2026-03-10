using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Interceptor that adds default headers to all requests.
    /// Headers are only added if they don't already exist on the request
    /// (unless overrideExisting is true).
    /// </summary>
    public sealed class DefaultHeadersInterceptor : IHttpInterceptor
    {
        private readonly HttpHeaders _defaultHeaders;
        private readonly bool _overrideExisting;

        public DefaultHeadersInterceptor(HttpHeaders defaultHeaders, bool overrideExisting = false)
        {
            _defaultHeaders = defaultHeaders ?? new HttpHeaders();
            _overrideExisting = overrideExisting;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                var requestForNext = request;

                if (_defaultHeaders.Count > 0)
                {
                    foreach (var name in _defaultHeaders.Names)
                    {
                        if (!_overrideExisting && request.Headers.Contains(name))
                            continue;

                        var values = _defaultHeaders.GetValues(name);
                        if (values.Count == 0)
                            continue;

                        if (ReferenceEquals(requestForNext, request))
                        {
                            requestForNext = request.Clone();
                            context.UpdateRequest(requestForNext);
                        }

                        requestForNext.Headers.Set(name, values[0]);
                        for (int i = 1; i < values.Count; i++)
                            requestForNext.Headers.Add(name, values[i]);
                    }
                }

                await next(requestForNext, handler, context, cancellationToken).ConfigureAwait(false);
            };
        }
    }
}
