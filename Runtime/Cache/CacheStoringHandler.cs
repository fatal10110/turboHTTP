using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Cache
{
    internal sealed class CacheStoringHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CacheInterceptor _owner;
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        private readonly string _baseKey;

        private int _statusCode;
        private HttpHeaders _headers;
        private SegmentedBuffer _responseBody;

        internal CacheStoringHandler(
            IHttpHandler inner,
            CacheInterceptor owner,
            UHttpRequest request,
            string baseKey,
            RequestContext context)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _requestMethod = request.Method;
            _requestUri = request.Uri;
            _requestHeaders = request.Headers.Clone();
            _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            _ = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            _statusCode = statusCode;
            _headers = headers?.Clone() ?? new HttpHeaders();
            if (body != null && body.TryGetBufferedData(out var buffered) && !buffered.IsEmpty)
            {
                if (_responseBody == null)
                    _responseBody = new SegmentedBuffer();

                _responseBody.Write(buffered.Span);
            }

            var capturedStatusCode = _statusCode;
            var capturedHeaders = _headers;
            var bodyToStore = _responseBody;
            _responseBody = null;

            if (capturedStatusCode == 0)
            {
                await _inner.OnResponseStartAsync(capturedStatusCode, capturedHeaders, body, context).ConfigureAwait(false);
                bodyToStore?.Dispose();
                return;
            }

            try
            {
                await _inner.OnResponseStartAsync(capturedStatusCode, capturedHeaders, body, context).ConfigureAwait(false);
            }
            catch
            {
                bodyToStore?.Dispose();
                throw;
            }

            try
            {
                _owner.QueueStoreResponse(_requestMethod, _requestUri, _requestHeaders, _baseKey, capturedStatusCode, capturedHeaders, bodyToStore);
                bodyToStore = null;
            }
            catch (Exception ex)
            {
                bodyToStore?.Dispose();
                Debug.WriteLine("[TurboHTTP][Cache] Failed to queue cache store: " + ex);
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _responseBody?.Dispose();
            _responseBody = null;
            _inner.OnResponseError(error, context);
        }
    }
}
