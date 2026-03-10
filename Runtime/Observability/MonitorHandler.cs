using System;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Observability
{
    internal sealed class MonitorHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly UHttpRequest _request;

        private int _statusCode;
        private HttpHeaders _headers;
        private SegmentedBuffer _responseBody;

        internal MonitorHandler(IHttpHandler inner, UHttpRequest request)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _request = request ?? throw new ArgumentNullException(nameof(request));
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
            try
            {
                _inner.OnResponseEnd(trailers, context);
            }
            finally
            {
                Capture(context, exception: null);
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            try
            {
                _inner.OnResponseError(error, context);
            }
            finally
            {
                Capture(context, error);
            }
        }

        private void Capture(RequestContext context, Exception exception)
        {
            try
            {
                UHttpResponse response = null;
                try
                {
                    var bodyBytes = _responseBody?.ToArray();
                    if (_statusCode != 0 || _headers != null || bodyBytes != null)
                    {
                        response = new UHttpResponse(
                            (System.Net.HttpStatusCode)_statusCode,
                            _headers ?? new HttpHeaders(),
                            bodyBytes,
                            context.Elapsed,
                            _request);
                    }

                    MonitorInterceptor.Capture(_request, response, context, exception);
                }
                finally
                {
                    response?.Dispose();
                }
            }
            finally
            {
                _responseBody?.Dispose();
                _responseBody = null;
            }
        }
    }
}
