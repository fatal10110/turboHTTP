using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    internal sealed class AdaptiveHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly NetworkQualityDetector _detector;
        private readonly long _requestBytes;
        private readonly TimeSpan _started;

        private long _responseBytes;
        private int _statusCode;

        internal AdaptiveHandler(
            IHttpHandler inner,
            NetworkQualityDetector detector,
            long requestBytes,
            TimeSpan started)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _requestBytes = requestBytes;
            _started = started;
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
            if (body != null && body.TryGetBufferedData(out var buffered))
                Interlocked.Add(ref _responseBytes, buffered.Length);

            await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);

            var elapsed = (context.Elapsed - _started).TotalMilliseconds;
            var responseBytes = Interlocked.Read(ref _responseBytes);
            _detector.AddSample(new NetworkQualitySample(
                latencyMs: elapsed,
                totalDurationMs: elapsed,
                wasTimeout: false,
                wasTransportFailure: false,
                bytesTransferred: _requestBytes + responseBytes,
                wasSuccess: _statusCode >= 200 && _statusCode < 300));
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            var elapsed = (context.Elapsed - _started).TotalMilliseconds;
            var errorType = error?.HttpError?.Type ?? UHttpErrorType.Unknown;
            var responseBytes = Interlocked.Read(ref _responseBytes);

            _detector.AddSample(new NetworkQualitySample(
                latencyMs: elapsed,
                totalDurationMs: elapsed,
                wasTimeout: errorType == UHttpErrorType.Timeout,
                wasTransportFailure: errorType == UHttpErrorType.NetworkError,
                bytesTransferred: _requestBytes + responseBytes,
                wasSuccess: false));

            _inner.OnResponseError(error, context);
        }
    }
}
