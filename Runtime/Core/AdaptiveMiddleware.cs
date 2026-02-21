using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Applies request timeout adaptation from recent network quality samples.
    /// </summary>
    public sealed class AdaptiveMiddleware : IHttpMiddleware
    {
        private readonly AdaptivePolicy _policy;
        private readonly NetworkQualityDetector _detector;

        public AdaptiveMiddleware(AdaptivePolicy policy, NetworkQualityDetector detector)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        }

        public async ValueTask<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (!_policy.Enable)
                return await next(request, context, cancellationToken);

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
                    requestForNext = request.WithTimeout(adaptedTimeout);
                    context.UpdateRequest(requestForNext);
                }
            }

            context.SetState("adaptive.quality", snapshot.Quality);
            context.SetState("adaptive.timeout_multiplier", timeoutMultiplier);
            context.SetState("adaptive.sample_count", snapshot.SampleCount);
            context.RecordEvent("adaptive.applied");

            var started = context.Elapsed;
            try
            {
                var response = await next(requestForNext, context, cancellationToken);

                var elapsed = (context.Elapsed - started).TotalMilliseconds;
                _detector.AddSample(new NetworkQualitySample(
                    latencyMs: elapsed,
                    totalDurationMs: elapsed,
                    wasTimeout: false,
                    wasTransportFailure: false,
                    bytesTransferred: (requestForNext.Body?.Length ?? 0) + response.Body.Length,
                    wasSuccess: response.IsSuccessStatusCode && !response.IsError));

                return response;
            }
            catch (UHttpException ex)
            {
                var elapsed = (context.Elapsed - started).TotalMilliseconds;
                var wasTimeout = ex.HttpError.Type == UHttpErrorType.Timeout;
                var transportFailure = ex.HttpError.Type == UHttpErrorType.NetworkError;

                _detector.AddSample(new NetworkQualitySample(
                    latencyMs: elapsed,
                    totalDurationMs: elapsed,
                    wasTimeout: wasTimeout,
                    wasTransportFailure: transportFailure,
                    bytesTransferred: requestForNext.Body?.Length ?? 0,
                    wasSuccess: false));

                throw;
            }
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
    }
}
