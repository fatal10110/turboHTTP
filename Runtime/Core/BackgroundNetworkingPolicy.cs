using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public sealed class BackgroundNetworkingPolicy
    {
        public bool Enable { get; set; }
        public int MaxQueuedRequests { get; set; } = 256;
        public TimeSpan GracePeriodBeforeQueue { get; set; } = TimeSpan.FromSeconds(2);
        public bool QueueOnAppPause { get; set; } = true;
        public bool RequireReplayableBodyForQueue { get; set; } = true;

        public BackgroundNetworkingPolicy Clone()
        {
            return new BackgroundNetworkingPolicy
            {
                Enable = Enable,
                MaxQueuedRequests = MaxQueuedRequests,
                GracePeriodBeforeQueue = GracePeriodBeforeQueue,
                QueueOnAppPause = QueueOnAppPause,
                RequireReplayableBodyForQueue = RequireReplayableBodyForQueue
            };
        }
    }

    public sealed class BackgroundNetworkingMiddleware : IHttpMiddleware
    {
        private readonly BackgroundNetworkingPolicy _policy;
        private readonly IBackgroundExecutionBridge _bridge;
        private readonly BoundedRequestQueue _queue;
        private int _replayed;
        private int _expired;
        private int _dropped;

        public BackgroundNetworkingMiddleware(
            BackgroundNetworkingPolicy policy,
            IBackgroundExecutionBridge bridge = null)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            if (_policy.MaxQueuedRequests <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    "MaxQueuedRequests must be > 0.");

            _bridge = bridge;
            _queue = new BoundedRequestQueue(_policy.MaxQueuedRequests);
        }

        public int Queued => _queue.Count;
        public int Replayed => Volatile.Read(ref _replayed);
        public int Expired => Volatile.Read(ref _expired);
        public int Dropped => Volatile.Read(ref _dropped);

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (!_policy.Enable)
                return await next(request, context, cancellationToken);

            IBackgroundExecutionScope scope = null;
            CancellationTokenSource linkedCts = null;
            var effectiveToken = cancellationToken;

            if (_bridge != null)
            {
                scope = await AcquireScopeSafelyAsync(context, cancellationToken).ConfigureAwait(false);
                if (scope != null)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        scope.ExpirationToken);
                    effectiveToken = linkedCts.Token;
                }
            }

            try
            {
                return await next(request, context, effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _policy.QueueOnAppPause &&
                ShouldQueueOnCancellation(scope, cancellationToken))
            {
                Interlocked.Increment(ref _expired);
                context.RecordEvent("mobile.bg.expired");

                var replayable = IsReplayable(request);
                if (!replayable)
                {
                    Interlocked.Increment(ref _dropped);
                    throw;
                }

                if (!_queue.TryEnqueue(request))
                {
                    Interlocked.Increment(ref _dropped);
                    throw;
                }

                if (_bridge is IDeferredBackgroundWorkBridge deferredBridge)
                {
                    var replayKey = GetReplayDedupeKey(request);
                    if (!string.IsNullOrWhiteSpace(replayKey) &&
                        deferredBridge.TryEnqueueDeferredWork(replayKey))
                    {
                        context.RecordEvent("mobile.bg.deferred.enqueued");
                    }
                }

                throw;
            }
            finally
            {
                linkedCts?.Dispose();
                if (scope != null)
                {
                    context.RecordEvent("mobile.bg.release");
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public bool TryDequeueReplayable(out UHttpRequest request)
        {
            if (_queue.TryDequeue(out request))
            {
                Interlocked.Increment(ref _replayed);
                if (_bridge is IDeferredBackgroundWorkBridge deferredBridge)
                {
                    var replayKey = GetReplayDedupeKey(request);
                    if (!string.IsNullOrWhiteSpace(replayKey))
                    {
                        deferredBridge.TryMarkReplayComplete(replayKey);
                    }
                }
                return true;
            }

            request = null;
            return false;
        }

        private sealed class BoundedRequestQueue
        {
            private readonly int _capacity;
            private readonly object _gate = new object();
            private readonly Queue<UHttpRequest> _inner = new Queue<UHttpRequest>();

            public BoundedRequestQueue(int capacity)
            {
                if (capacity <= 0)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "Must be > 0.");
                _capacity = capacity;
            }

            public int Count
            {
                get
                {
                    lock (_gate)
                    {
                        return _inner.Count;
                    }
                }
            }

            public bool TryEnqueue(UHttpRequest request)
            {
                lock (_gate)
                {
                    if (_inner.Count >= _capacity)
                        return false;
                    _inner.Enqueue(request);
                    return true;
                }
            }

            public bool TryDequeue(out UHttpRequest request)
            {
                lock (_gate)
                {
                    if (_inner.Count == 0)
                    {
                        request = null;
                        return false;
                    }

                    request = _inner.Dequeue();
                    return true;
                }
            }
        }

        private bool IsReplayable(UHttpRequest request)
        {
            if (request == null)
                return false;

            if (request.Method == HttpMethod.GET || request.Method == HttpMethod.HEAD)
                return true;

            if (!_policy.RequireReplayableBodyForQueue)
                return true;

            return request.Body != null;
        }

        private static bool ShouldQueueOnCancellation(
            IBackgroundExecutionScope scope,
            CancellationToken requestToken)
        {
            if (scope == null)
                return false;

            return scope.ExpirationToken.IsCancellationRequested &&
                   !requestToken.IsCancellationRequested;
        }

        private static string GetReplayDedupeKey(UHttpRequest request)
        {
            if (request == null)
                return null;

            if (request.Metadata != null &&
                request.Metadata.TryGetValue(RequestMetadataKeys.BackgroundReplayDedupeKey, out var value) &&
                value is string configured &&
                !string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var uri = request.Uri?.ToString() ?? string.Empty;
            return request.Method.ToUpperString() + ":" + uri;
        }

        private async Task<IBackgroundExecutionScope> AcquireScopeSafelyAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                context.RecordEvent("mobile.bg.acquire.start");
                var scope = await _bridge.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
                if (scope != null)
                {
                    context.RecordEvent("mobile.bg.acquire.success");
                }

                return scope;
            }
            catch
            {
                // Background acquisition is best effort and must not break request flow.
                return null;
            }
        }
    }
}
