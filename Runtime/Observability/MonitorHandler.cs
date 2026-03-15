using System;
using System.Buffers;
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
        private int _bufferedCaptureLimit;
        private int _bufferedResponseBytes;
        private long _totalResponseBytes;
        private bool _responseBodyWasTruncated;

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
            _bufferedCaptureLimit = MonitorInterceptor.GetBufferedResponseCaptureLimit(headers);
            _bufferedResponseBytes = 0;
            _totalResponseBytes = 0;
            _responseBodyWasTruncated = false;
            _inner.OnResponseStart(statusCode, headers, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (!chunk.IsEmpty)
            {
                _totalResponseBytes += chunk.Length;

                if (_bufferedResponseBytes < _bufferedCaptureLimit)
                {
                    if (_responseBody == null)
                        _responseBody = new SegmentedBuffer();

                    var remaining = _bufferedCaptureLimit - _bufferedResponseBytes;
                    var bytesToBuffer = Math.Min(chunk.Length, remaining);
                    if (bytesToBuffer > 0)
                    {
                        _responseBody.Write(chunk.Slice(0, bytesToBuffer));
                        _bufferedResponseBytes += bytesToBuffer;
                    }
                }

                if (_totalResponseBytes > _bufferedCaptureLimit)
                    _responseBodyWasTruncated = true;
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
                var body = _responseBody != null
                    ? _responseBody.AsSequence()
                    : ReadOnlySequence<byte>.Empty;
                MonitorInterceptor.Capture(
                    _request,
                    _statusCode,
                    _headers,
                    body,
                    _totalResponseBytes > int.MaxValue ? int.MaxValue : (int)_totalResponseBytes,
                    _responseBodyWasTruncated,
                    context,
                    exception);
            }
            finally
            {
                _responseBody?.Dispose();
                _responseBody = null;
                _bufferedCaptureLimit = 0;
                _bufferedResponseBytes = 0;
                _totalResponseBytes = 0;
                _responseBodyWasTruncated = false;
            }
        }
    }
}
