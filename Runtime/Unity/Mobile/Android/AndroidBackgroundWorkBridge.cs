using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Holds an in-process guard while a request runs and hands expired replayable requests to
    /// WorkManager. Experimental: the Java plugin (<c>Plugins/Android/TurboHttpBackground.androidlib</c>)
    /// and its JNI calls are included only in Android players built with the
    /// <c>TURBOHTTP_BACKGROUND_NETWORKING</c> scripting define; elsewhere no scope is acquired and the
    /// deferred-work queue is tracked in memory only.
    /// </summary>
    /// <remarks>
    /// Every plugin call is made through JNI on the calling thread (attached to the VM first), never
    /// through <see cref="MainThreadDispatcher"/>, so nothing waits on the player loop and no worker
    /// blocks on the main thread (#88, #181).
    /// </remarks>
    public sealed class AndroidBackgroundWorkBridge : IBackgroundExecutionBridge, IDeferredBackgroundWorkBridge
    {
        private readonly AndroidBackgroundWorkConfig _config;
        private readonly IAndroidBackgroundPlugin _plugin;
        private readonly ConcurrentDictionary<string, byte> _queuedWorkIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>Creates a bridge over the platform plugin (none outside opted-in Android players).</summary>
        /// <param name="config">Bridge configuration (cloned); <see langword="null"/> uses the defaults.</param>
        public AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config = null)
            : this(config, null, usePlatformPlugin: true)
        {
        }

        internal AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config, IAndroidBackgroundPlugin plugin)
            : this(config, plugin, usePlatformPlugin: false)
        {
        }

        private AndroidBackgroundWorkBridge(
            AndroidBackgroundWorkConfig config,
            IAndroidBackgroundPlugin plugin,
            bool usePlatformPlugin)
        {
            _config = (config ?? new AndroidBackgroundWorkConfig()).Clone();
            _plugin = usePlatformPlugin ? AndroidBackgroundPluginJni.Create(_config.PluginClassName) : plugin;
        }

        /// <inheritdoc />
        public ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var plugin = _plugin;
            if (plugin == null)
                return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);

            var scopeId = "android-" + Guid.NewGuid().ToString("N");
            try
            {
                if (!plugin.EnsureInitialized() || !plugin.BeginGuard(scopeId, _config.GuardGraceMilliseconds))
                    return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);
            }
            catch
            {
                // Plugin unavailable: fall back to foreground semantics (phase 14.3 step 2).
                return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);
            }

            var graceMilliseconds = _config.GuardGraceMilliseconds;
            IBackgroundExecutionScope scope = new Mobile.BackgroundExecutionScope(
                scopeId,
                DateTime.UtcNow,
                CancellationToken.None,
                remainingBudgetProvider: () => TimeSpan.FromMilliseconds(graceMilliseconds),
                disposeAction: () =>
                {
                    try
                    {
                        plugin.EndGuard(scopeId);
                    }
                    catch
                    {
                        // The guard is advisory; a failed end leaves only a stale map entry in the plugin.
                    }

                    return default;
                });
            return new ValueTask<IBackgroundExecutionScope>(scope);
        }

        /// <inheritdoc />
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

            var plugin = _plugin;
            if (plugin == null)
                return true;

            try
            {
                if (plugin.EnsureInitialized() && plugin.EnqueueDeferredWork(dedupeKey))
                    return true;
            }
            catch
            {
                // Falls through: the key is released so a later attempt can enqueue it.
            }

            _queuedWorkIds.TryRemove(dedupeKey, out _);
            return false;
        }

        /// <inheritdoc />
        public bool TryMarkReplayComplete(string dedupeKey)
        {
            if (string.IsNullOrWhiteSpace(dedupeKey))
                return false;

            var removed = _queuedWorkIds.TryRemove(dedupeKey, out _);
            var plugin = _plugin;
            if (plugin != null)
            {
                try
                {
                    plugin.CancelDeferredWork(dedupeKey);
                }
                catch
                {
                    // WorkManager keeps retrying the placeholder job; the replay itself already happened.
                }
            }

            return removed;
        }
    }
}
