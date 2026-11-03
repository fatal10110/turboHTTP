using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Holds an in-process guard while a request runs. A scope expires when the app has been paused
    /// for <see cref="AndroidBackgroundWorkConfig.BackgroundBudgetMilliseconds"/> (Android has no
    /// <c>backgroundTimeRemaining</c>); <see cref="BackgroundNetworkingInterceptor"/> then cancels the
    /// in-flight request and hands a replayable one to WorkManager through
    /// <see cref="TryEnqueueDeferredWork"/> (#267). Experimental: the Java plugin (<c>Plugins/Android/TurboHttpBackground.androidlib</c>)
    /// and its JNI calls are included only in Android players built with the
    /// <c>TURBOHTTP_BACKGROUND_NETWORKING</c> scripting define; elsewhere no scope is acquired and the
    /// deferred-work queue is tracked in memory only.
    /// </summary>
    /// <remarks>
    /// Every plugin call is made through JNI on the calling thread (attached to the VM first), never
    /// through <see cref="MainThreadDispatcher"/>, so nothing waits on the player loop and no worker
    /// blocks on the main thread (#88, #181). The expiry budget is timed on the thread pool from the
    /// player's pause notification, so it fires while the player loop is paused; the bridge owns the
    /// expiration token every scope carries and cancels it when the budget is spent. Returning to the
    /// foreground gives later scopes a fresh token. Expiry behavior on devices is tracked in #266.
    /// </remarks>
    public sealed class AndroidBackgroundWorkBridge : IBackgroundExecutionBridge, IDeferredBackgroundWorkBridge
    {
        private readonly AndroidBackgroundWorkConfig _config;
        private readonly IAndroidBackgroundPlugin _plugin;
        private readonly ConcurrentDictionary<string, byte> _queuedWorkIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly IAndroidBackgroundExpirySignal _expirySignal;
        private readonly object _expiryGate = new object();

        // Owned by _expiryGate. One source per foreground episode: cancelled when the budget is
        // spent, replaced (never reset) on return to the foreground because scopes hold its token.
        // A cancelled source has no timer or wait handle, so a replaced one is left to the GC.
        private CancellationTokenSource _expiry;
        private bool _expiryCancelled;

        /// <summary>Creates a bridge over the platform plugin (none outside opted-in Android players).</summary>
        /// <param name="config">Bridge configuration (cloned); <see langword="null"/> uses the defaults.</param>
        public AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config = null)
            : this(config, null, null, usePlatformPlugin: true)
        {
        }

        internal AndroidBackgroundWorkBridge(AndroidBackgroundWorkConfig config, IAndroidBackgroundPlugin plugin)
            : this(config, plugin, null, usePlatformPlugin: false)
        {
        }

        internal AndroidBackgroundWorkBridge(
            AndroidBackgroundWorkConfig config,
            IAndroidBackgroundPlugin plugin,
            IAndroidBackgroundExpirySignal expirySignal)
            : this(config, plugin, expirySignal, usePlatformPlugin: false)
        {
        }

        private AndroidBackgroundWorkBridge(
            AndroidBackgroundWorkConfig config,
            IAndroidBackgroundPlugin plugin,
            IAndroidBackgroundExpirySignal expirySignal,
            bool usePlatformPlugin)
        {
            _config = (config ?? new AndroidBackgroundWorkConfig()).Clone();
            _plugin = usePlatformPlugin ? AndroidBackgroundPluginJni.Create(_config.PluginClassName) : plugin;

            // Without a plugin no scope is ever acquired, so only a platform bridge listens for pauses.
            if (usePlatformPlugin && _plugin != null)
                expirySignal = new AndroidPauseExpirySignal(_config.BackgroundBudgetMilliseconds);

            _expirySignal = expirySignal;
            if (expirySignal != null)
            {
                _expiry = new CancellationTokenSource();
                expirySignal.ExpiryChanged += OnExpiryChanged;
                OnExpiryChanged();
            }
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
            var expirySignal = _expirySignal;
            var expirationToken = CancellationToken.None;
            if (expirySignal != null)
            {
                lock (_expiryGate)
                {
                    expirationToken = _expiry.Token;
                }
            }

            IBackgroundExecutionScope scope = new Mobile.BackgroundExecutionScope(
                scopeId,
                DateTime.UtcNow,
                expirationToken,
                remainingBudgetProvider: expirySignal != null
                    ? () => expirySignal.RemainingBudget
                    : (Func<TimeSpan>)(() => TimeSpan.FromMilliseconds(graceMilliseconds)),
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

        private void OnExpiryChanged()
        {
            CancellationTokenSource toCancel = null;
            lock (_expiryGate)
            {
                if (_expirySignal.IsExpired)
                {
                    if (!_expiryCancelled)
                    {
                        _expiryCancelled = true;
                        toCancel = _expiry;
                    }
                }
                else if (_expiryCancelled)
                {
                    _expiry = new CancellationTokenSource();
                    _expiryCancelled = false;
                }
            }

            // Outside the lock: cancellation runs the in-flight requests' callbacks synchronously.
            // A resume that raced in has already given later scopes a fresh source.
            if (toCancel != null)
            {
                try
                {
                    toCancel.Cancel();
                }
                catch (AggregateException)
                {
                    // A registration threw; the token is cancelled regardless.
                }
            }
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
