using System;
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

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _statusCode = statusCode;
            _headers = headers?.Clone() ?? new HttpHeaders();
            _inner.OnResponseStart(statusCode, headers, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (!chunk.IsEmpty)
            {
                if (_responseBody == null)
                    _responseBody = new SegmentedBuffer();

                _responseBody.Write(chunk);
            }

            _inner.OnResponseData(chunk, context);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            var statusCode = _statusCode;
            var headers = _headers;
            var bodyToStore = _responseBody;
            _responseBody = null;

            if (statusCode == 0)
            {
                try
                {
                    _inner.OnResponseEnd(trailers, context);
                }
                finally
                {
                    bodyToStore?.Dispose();
                }

                return;
            }

            try
            {
                _inner.OnResponseEnd(trailers, context);
            }
            catch
            {
                bodyToStore?.Dispose();
                throw;
            }

            _owner.QueueStoreResponse(_requestMethod, _requestUri, _requestHeaders, _baseKey, statusCode, headers, bodyToStore);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _responseBody?.Dispose();
            _responseBody = null;
            _inner.OnResponseError(error, context);
        }
    }
}
