using System;
using System.Threading;

namespace TurboHTTP.Core
{
    public sealed class BackgroundRequestQueuedException : OperationCanceledException
    {
        public BackgroundRequestQueuedException(
            string replayDedupeKey,
            string scopeId,
            CancellationToken cancellationToken)
            : base(
                "Request was canceled after background expiration and queued for deferred replay.",
                cancellationToken)
        {
            ReplayDedupeKey = replayDedupeKey;
            ScopeId = scopeId;
        }

        public string ReplayDedupeKey { get; }
        public string ScopeId { get; }
    }

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
}
