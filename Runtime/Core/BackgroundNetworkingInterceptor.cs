using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public sealed class BackgroundNetworkingInterceptor : IHttpInterceptor
    {
        private readonly BackgroundNetworkingPolicy _policy;
        private readonly IBackgroundExecutionBridge _bridge;
        private readonly BoundedRequestQueue _queue;
        private int _replayed;
        private int _expired;
        private int _dropped;

        public BackgroundNetworkingInterceptor(
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

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                if (!_policy.Enable)
                    return next(request, handler, context, cancellationToken);

                return InvokeWithBackgroundPolicyAsync(next, request, handler, context, cancellationToken);
            };
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
                        deferredBridge.TryMarkReplayComplete(replayKey);
                }

                return true;
            }

            request = null;
            return false;
        }

        private async Task InvokeWithBackgroundPolicyAsync(
            DispatchFunc next,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken)
        {
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
                await next(request, handler, context, effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _policy.QueueOnAppPause &&
                ShouldQueueOnCancellation(scope, cancellationToken))
            {
                Interlocked.Increment(ref _expired);
                context.RecordEvent("mobile.bg.expired");

                if (!IsReplayable(request))
                {
                    Interlocked.Increment(ref _dropped);
                    throw;
                }

                if (!_queue.TryEnqueue(request.Clone()))
                {
                    Interlocked.Increment(ref _dropped);
                    throw;
                }

                var replayKey = GetReplayDedupeKey(request);
                if (_bridge is IDeferredBackgroundWorkBridge deferredBridge &&
                    !string.IsNullOrWhiteSpace(replayKey) &&
                    deferredBridge.TryEnqueueDeferredWork(replayKey))
                {
                    context.RecordEvent("mobile.bg.deferred.enqueued");
                }

                throw new BackgroundRequestQueuedException(
                    replayKey,
                    scope?.ScopeId,
                    effectiveToken);
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

        private async Task<IBackgroundExecutionScope> AcquireScopeSafelyAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                context.RecordEvent("mobile.bg.acquire.start");
                var scope = await _bridge.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
                if (scope != null)
                    context.RecordEvent("mobile.bg.acquire.success");

                return scope;
            }
            catch
            {
                return null;
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

            return !request.Body.IsEmpty;
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

        private sealed class BoundedRequestQueue
        {
            private readonly int _capacity;
            private readonly object _gate = new object();
            private readonly Queue<UHttpRequest> _inner = new Queue<UHttpRequest>();

            internal BoundedRequestQueue(int capacity)
            {
                if (capacity <= 0)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "Must be > 0.");
                _capacity = capacity;
            }

            internal int Count
            {
                get
                {
                    lock (_gate)
                    {
                        return _inner.Count;
                    }
                }
            }

            internal bool TryEnqueue(UHttpRequest request)
            {
                lock (_gate)
                {
                    if (_inner.Count >= _capacity)
                        return false;

                    _inner.Enqueue(request);
                    return true;
                }
            }

            internal bool TryDequeue(out UHttpRequest request)
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
    }
}
