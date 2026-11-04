using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.iOS
{
    /// <summary>
    /// Holds a UIKit background task (<c>beginBackgroundTaskWithName:expirationHandler:</c>) while a
    /// request runs. Experimental: the native bridge is compiled and linked only in iOS players built with
    /// the <c>TURBOHTTP_BACKGROUND_NETWORKING</c> scripting define; elsewhere no scope is acquired and
    /// requests keep foreground semantics.
    /// </summary>
    /// <remarks>
    /// Begin and end call UIKit directly from the calling thread (both are documented thread-safe), never
    /// through <see cref="MainThreadDispatcher"/>, so they work while the player loop is paused. The scope's
    /// <see cref="IBackgroundExecutionScope.ExpirationToken"/> is cancelled only by UIKit's expiration
    /// handler, which runs only while the app is in the background; a foreground request is never expired
    /// by a timer.
    /// </remarks>
    public sealed class IosBackgroundTaskBridge : IBackgroundExecutionBridge
    {
        private const string TaskName = "TurboHTTP.Request";

        // Live scopes by cookie. An entry is removed exactly once: by the expiration callback (which then
        // cancels it) or by the scope's dispose (which then disposes it). Bounded by in-flight requests.
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveScopes =
            new ConcurrentDictionary<int, CancellationTokenSource>();

        private static int s_nextCookie;

        private readonly IIosBackgroundTaskNative _native;

        /// <summary>Creates a bridge over the platform's native bindings (none outside opted-in iOS players).</summary>
        public IosBackgroundTaskBridge()
            : this(IosBackgroundTaskBindings.Platform)
        {
        }

        internal IosBackgroundTaskBridge(IIosBackgroundTaskNative native)
        {
            _native = native;
        }

        /// <inheritdoc />
        public ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (_native == null)
                return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);

            var cookie = Interlocked.Increment(ref s_nextCookie);
            var expiration = new CancellationTokenSource();
            if (!ActiveScopes.TryAdd(cookie, expiration))
            {
                expiration.Dispose();
                return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);
            }

            bool started;
            try
            {
                started = _native.Begin(TaskName, cookie);
            }
            catch
            {
                // Native bridge unavailable: fall back to foreground semantics (phase 14.3 step 1).
                started = false;
            }

            if (!started)
            {
                if (ActiveScopes.TryRemove(cookie, out _))
                    expiration.Dispose();
                return new ValueTask<IBackgroundExecutionScope>((IBackgroundExecutionScope)null);
            }

            var native = _native;
            var token = expiration.Token;
            IBackgroundExecutionScope scope = new Mobile.BackgroundExecutionScope(
                scopeId: "ios-" + cookie,
                startedAtUtc: DateTime.UtcNow,
                expirationToken: token,
                remainingBudgetProvider: () => GetRemainingBudget(native, token),
                disposeAction: () =>
                {
                    var owned = ActiveScopes.TryRemove(cookie, out _);
                    try
                    {
                        native.End(cookie);
                    }
                    catch
                    {
                        // The native side ends the task on expiry; nothing else to release here.
                    }

                    // After an expiry the callback took the entry and cancelled it; it is left to the GC
                    // (no timer, no wait handle) because the request may still observe its token.
                    if (owned)
                        expiration.Dispose();
                    return default;
                });
            return new ValueTask<IBackgroundExecutionScope>(scope);
        }

        /// <summary>
        /// Called by the native expiration handler (main thread, app in background) for a live scope:
        /// cancels its expiration token so the interceptor can queue the request for replay.
        /// </summary>
        internal static void HandleNativeExpiration(int cookie)
        {
            if (!ActiveScopes.TryRemove(cookie, out var expiration))
                return;

            try
            {
                expiration.Cancel();
            }
            catch (AggregateException)
            {
                // A registration threw; the token is cancelled regardless.
            }
        }

        private static TimeSpan GetRemainingBudget(IIosBackgroundTaskNative native, CancellationToken expiration)
        {
            if (expiration.IsCancellationRequested)
                return TimeSpan.Zero;

            try
            {
                return IosBackgroundTaskBindings.ToRemainingBudget(native.GetRemainingSeconds());
            }
            catch
            {
                // Fail open: an unreadable budget is not an expiry (#87).
                return TimeSpan.MaxValue;
            }
        }
    }
}
