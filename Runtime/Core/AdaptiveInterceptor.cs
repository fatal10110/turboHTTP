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

            Task dispatchTask;
            try
            {
                dispatchTask = next(
                    requestForNext,
                    new AdaptiveHandler(handler, _detector, requestForNext.Body.Length, context.Elapsed),
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
