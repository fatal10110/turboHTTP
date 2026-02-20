using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.Android
{
    public sealed class AndroidBackgroundWorkBridge : IBackgroundExecutionBridge, IDeferredBackgroundWorkBridge
    {
        private readonly AndroidBackgroundWorkConfig _config;
        private readonly ConcurrentDictionary<string, byte> _queuedWorkIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        public AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config = null)
        {
            _config = (config ?? new AndroidBackgroundWorkConfig()).Clone();
        }

        public ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);
#else
            TryInitializePluginContext();

            var scopeId = "android-" + Guid.NewGuid().ToString("N");
            var started = DateTime.UtcNow;
            var expirationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                plugin.CallStatic("beginInProcessGuard", scopeId, _config.GuardGraceMilliseconds);
            }
            catch
            {
                // Plugin may be unavailable; degrade gracefully.
            }

            var scope = new Mobile.BackgroundExecutionScope(
                scopeId,
                started,
                expirationCts.Token,
                remainingBudgetProvider: () => TimeSpan.FromMilliseconds(_config.GuardGraceMilliseconds),
                disposeAction: () =>
                {
                    try
                    {
                        using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                        plugin.CallStatic("endInProcessGuard", scopeId);
                    }
                    catch
                    {
                    }

                    expirationCts.Cancel();
                    expirationCts.Dispose();
                    return default;
                });

            return new ValueTask<IBackgroundExecutionScope>(scope);
#endif
        }

        public bool TryEnqueueDeferredWork(string dedupeKey)
        {
            if (string.IsNullOrWhiteSpace(dedupeKey))
                return false;
            if (!_config.EnableDeferredWork)
                return false;
            if (_queuedWorkIds.Count >= _config.MaxQueuedRequests)
                return false;

            if (!_queuedWorkIds.TryAdd(dedupeKey, 0))
                return false;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                TryInitializePluginContext();
                using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                var enqueued = plugin.CallStatic<bool>("enqueueDeferredWork", dedupeKey);
                if (!enqueued)
                {
                    _queuedWorkIds.TryRemove(dedupeKey, out _);
                    return false;
                }
            }
            catch
            {
                _queuedWorkIds.TryRemove(dedupeKey, out _);
                return false;
            }
#endif
            return true;
        }

        public bool TryMarkReplayComplete(string dedupeKey)
        {
            if (string.IsNullOrWhiteSpace(dedupeKey))
                return false;

            var removed = _queuedWorkIds.TryRemove(dedupeKey, out _);
#if UNITY_ANDROID && !UNITY_EDITOR
            TryCancelDeferredWorkInPlugin(dedupeKey);
#endif
            return removed;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void TryInitializePluginContext()
        {
            try
            {
                using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                using var unityPlayer = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity");
                plugin.CallStatic<bool>("initialize", activity);
            }
            catch
            {
            }
        }

        private void TryCancelDeferredWorkInPlugin(string dedupeKey)
        {
            try
            {
                using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                plugin.CallStatic<bool>("cancelDeferredWork", dedupeKey);
            }
            catch
            {
            }
        }
#endif
    }
}
