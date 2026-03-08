using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Applies request timeout adaptation from recent network quality samples.
    /// </summary>
    public sealed class AdaptiveInterceptor : IHttpInterceptor
    {
        private readonly AdaptivePolicy _policy;
        private readonly NetworkQualityDetector _detector;

        public AdaptiveInterceptor(AdaptivePolicy policy, NetworkQualityDetector detector)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (!_policy.Enable)
                    return next(request, handler, context, cancellationToken);

                return InvokeAdaptiveAsync(next, request, handler, context, cancellationToken);
            };
        }

        private async Task InvokeAdaptiveAsync(
            DispatchFunc next,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var snapshot = _detector.GetSnapshot();
            var explicitTimeout = request.Metadata.TryGetValue(RequestMetadataKeys.ExplicitTimeout, out var explicitTimeoutFlag)
                && explicitTimeoutFlag is bool value
                && value;

            var timeoutMultiplier = GetTimeoutMultiplier(snapshot.Quality);
            var requestForNext = request;

            if (!explicitTimeout)
            {
                var adapted = request.Timeout.TotalMilliseconds * timeoutMultiplier;
                adapted = Math.Max(_policy.MinTimeout.TotalMilliseconds, adapted);
                adapted = Math.Min(_policy.MaxTimeout.TotalMilliseconds, adapted);

                var adaptedTimeout = TimeSpan.FromMilliseconds(adapted);
                if (adaptedTimeout != request.Timeout)
                {
                    requestForNext = request.Clone();
                    requestForNext.SetTimeoutInternal(adaptedTimeout);
                    context.UpdateRequest(requestForNext);
                }
            }

            context.SetState("adaptive.quality", snapshot.Quality);
            context.SetState("adaptive.timeout_multiplier", timeoutMultiplier);
            context.SetState("adaptive.sample_count", snapshot.SampleCount);
            context.RecordEvent("adaptive.applied");

            await next(
                requestForNext,
                new AdaptiveHandler(handler, _detector, requestForNext.Body.Length, context.Elapsed),
                context,
                cancellationToken).ConfigureAwait(false);
        }

        private static double GetTimeoutMultiplier(NetworkQuality quality)
        {
            switch (quality)
            {
                case NetworkQuality.Excellent:
                    return 0.8;
                case NetworkQuality.Good:
                    return 1.0;
                case NetworkQuality.Fair:
                    return 1.5;
                case NetworkQuality.Poor:
                default:
                    return 2.0;
            }
        }

        private sealed class AdaptiveHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private readonly NetworkQualityDetector _detector;
            private readonly long _requestBytes;
            private readonly TimeSpan _started;

            private long _responseBytes;
            private int _statusCode;

            public AdaptiveHandler(
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

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                _statusCode = statusCode;
                _inner.OnResponseStart(statusCode, headers, context);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                Interlocked.Add(ref _responseBytes, (long)chunk.Length);
                _inner.OnResponseData(chunk, context);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                var elapsed = (context.Elapsed - _started).TotalMilliseconds;
                var responseBytes = Interlocked.Read(ref _responseBytes);
                _detector.AddSample(new NetworkQualitySample(
                    latencyMs: elapsed,
                    totalDurationMs: elapsed,
                    wasTimeout: false,
                    wasTransportFailure: false,
                    bytesTransferred: _requestBytes + responseBytes,
                    wasSuccess: _statusCode >= 200 && _statusCode < 300));

                _inner.OnResponseEnd(trailers, context);
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
}
