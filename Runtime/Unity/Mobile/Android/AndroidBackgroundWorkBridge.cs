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
        private int _pluginInitializationAttempted;
        private int _pluginInitialized;

        public AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config = null)
        {
            _config = (config ?? new AndroidBackgroundWorkConfig()).Clone();
        }

        public async ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            return null;
#else
            await TryInitializePluginContextAsync(cancellationToken).ConfigureAwait(false);

            var scopeId = "android-" + Guid.NewGuid().ToString("N");
            var started = DateTime.UtcNow;
            var expirationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                await TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(() =>
                {
                    using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                    plugin.CallStatic("beginInProcessGuard", scopeId, _config.GuardGraceMilliseconds);
                }, cancellationToken).ConfigureAwait(false);
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
                disposeAction: async () =>
                {
                    try
                    {
                        await TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(() =>
                        {
                            using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                            plugin.CallStatic("endInProcessGuard", scopeId);
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    expirationCts.Cancel();
                    expirationCts.Dispose();
                });

            return scope;
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
                TryInitializePluginContextBlocking();
                var enqueued = TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(() =>
                {
                    using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                    return plugin.CallStatic<bool>("enqueueDeferredWork", dedupeKey);
                }).GetAwaiter().GetResult();
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
            if (Volatile.Read(ref _pluginInitializationAttempted) != 0)
                return;

            if (Interlocked.CompareExchange(ref _pluginInitializationAttempted, 1, 0) != 0)
                return;

            try
            {
                using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                using var unityPlayer = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity");
                if (plugin.CallStatic<bool>("initialize", activity))
                {
                    Volatile.Write(ref _pluginInitialized, 1);
                }
            }
            catch
            {
            }
        }

        private async Task TryInitializePluginContextAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Volatile.Read(ref _pluginInitializationAttempted) != 0)
                    return;

                await TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(
                    TryInitializePluginContext,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private void TryInitializePluginContextBlocking()
        {
            try
            {
                if (Volatile.Read(ref _pluginInitializationAttempted) != 0)
                    return;

                TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(
                    TryInitializePluginContext).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        private void TryCancelDeferredWorkInPlugin(string dedupeKey)
        {
            try
            {
                if (Volatile.Read(ref _pluginInitialized) == 0)
                    return;

                TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(() =>
                {
                    using var plugin = new UnityEngine.AndroidJavaClass(_config.PluginClassName);
                    plugin.CallStatic<bool>("cancelDeferredWork", dedupeKey);
                }).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
#endif
    }
}
