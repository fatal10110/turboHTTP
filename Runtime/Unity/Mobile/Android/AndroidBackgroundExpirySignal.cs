using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// When the Android background budget is spent (#267). Android has no
    /// <c>backgroundTimeRemaining</c> equivalent, so the budget is app-defined: it starts when the
    /// app pauses and is spent after <see cref="AndroidBackgroundWorkConfig.BackgroundBudgetMilliseconds"/>.
    /// </summary>
    internal interface IAndroidBackgroundExpirySignal
    {
        /// <summary>The app is paused and its background budget is spent.</summary>
        bool IsExpired { get; }

        /// <summary>The budget left: the full budget in the foreground, zero once expired.</summary>
        TimeSpan RemainingBudget { get; }

        /// <summary>
        /// Raised, on any thread, when <see cref="IsExpired"/> may have changed; handlers read
        /// <see cref="IsExpired"/> rather than trusting the order of concurrent raises.
        /// </summary>
        event Action ExpiryChanged;
    }

    /// <summary>
    /// Pause notifications from the player (<c>OnApplicationPause</c>, reported by
    /// <c>AndroidAppPauseListener</c> in opted-in Android players). The notification is the last
    /// main-thread work before the player loop stops, so nothing after it may depend on the loop.
    /// The source records when the pause began, so a signal that starts listening mid-pause still
    /// times the budget from the pause.
    /// </summary>
    internal static class AndroidAppPauseSource
    {
        private static readonly object Gate = new object();

        // Owned by Gate.
        private static bool _paused;
        private static long _pausedAtTimestamp;

        internal static event Action<bool> PauseChanged;

        internal static bool IsPaused
        {
            get
            {
                lock (Gate)
                {
                    return _paused;
                }
            }
        }

        /// <summary>Test seam: how many listeners the static event roots.</summary>
        internal static int ListenerCount => PauseChanged?.GetInvocationList().Length ?? 0;

        /// <summary>The <see cref="Stopwatch"/> timestamp the current pause began at, if paused.</summary>
        internal static bool TryGetPauseStart(out long pausedAtTimestamp)
        {
            lock (Gate)
            {
                pausedAtTimestamp = _pausedAtTimestamp;
                return _paused;
            }
        }

        internal static void Report(bool paused)
        {
            lock (Gate)
            {
                if (_paused == paused)
                    return;

                _paused = paused;
                if (paused)
                    _pausedAtTimestamp = Stopwatch.GetTimestamp();
            }

            // Outside the lock; listeners re-read the state rather than trust the argument.
            PauseChanged?.Invoke(paused);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetStaticState()
        {
            lock (Gate)
            {
                _paused = false;
                _pausedAtTimestamp = 0;
            }

            PauseChanged = null;
        }
    }

    /// <summary>
    /// The default signal: the budget starts at <see cref="AndroidAppPauseSource"/>'s pause
    /// notification and ends <c>budgetMilliseconds</c> later. <see cref="IsExpired"/> is computed
    /// from the pause start, so it is right at any time. Only while <see cref="ExpiryChanged"/> has
    /// a handler does the signal listen to the static source and time the budget with a
    /// <see cref="Timer"/> on the thread pool (which keeps running while the player loop is paused,
    /// #88); without handlers nothing static roots it or its handlers' owners.
    /// </summary>
    internal sealed class AndroidPauseExpirySignal : IAndroidBackgroundExpirySignal
    {
        private readonly object _gate = new object();
        private readonly long _budgetTicks;
        private readonly int _budgetMilliseconds;
        private readonly Action<bool> _onPauseChanged;
        private readonly TimerCallback _onBudgetElapsed;

        // Owned by _gate.
        private Action _expiryChanged;
        private bool _attached;
        private bool _reportedExpired;
        private Timer _timer;
        private int _generation;

        internal AndroidPauseExpirySignal(int budgetMilliseconds)
        {
            _budgetMilliseconds = Math.Max(0, budgetMilliseconds);
            _budgetTicks = (long)(_budgetMilliseconds * (double)Stopwatch.Frequency / 1000d);
            _onPauseChanged = OnPauseChanged;
            _onBudgetElapsed = OnBudgetElapsed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The first handler attaches the signal to <see cref="AndroidAppPauseSource"/>; removing
        /// the last detaches it and stops its timer. Adding a handler does not raise the event:
        /// the subscriber reads <see cref="IsExpired"/> itself.
        /// </remarks>
        public event Action ExpiryChanged
        {
            add
            {
                lock (_gate)
                {
                    _expiryChanged += value;
                    if (!_attached && _expiryChanged != null)
                    {
                        _attached = true;
                        AndroidAppPauseSource.PauseChanged += _onPauseChanged;
                        _reportedExpired = ComputeExpired(out _);
                        RescheduleLocked();
                    }
                }
            }
            remove
            {
                lock (_gate)
                {
                    _expiryChanged -= value;
                    if (_attached && _expiryChanged == null)
                    {
                        _attached = false;
                        AndroidAppPauseSource.PauseChanged -= _onPauseChanged;
                        _generation++;
                        _timer?.Dispose();
                        _timer = null;
                    }
                }
            }
        }

        public bool IsExpired => ComputeExpired(out _);

        public TimeSpan RemainingBudget
        {
            get
            {
                if (!AndroidAppPauseSource.TryGetPauseStart(out var pausedAt))
                    return TimeSpan.FromMilliseconds(_budgetMilliseconds);

                var left = pausedAt + _budgetTicks - Stopwatch.GetTimestamp();
                return left <= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency);
            }
        }

        // Expired: paused and the budget since the pause is spent. remainingTicks > 0 only while
        // paused and not yet expired.
        private bool ComputeExpired(out long remainingTicks)
        {
            remainingTicks = 0;
            if (!AndroidAppPauseSource.TryGetPauseStart(out var pausedAt))
                return false;

            remainingTicks = pausedAt + _budgetTicks - Stopwatch.GetTimestamp();
            return remainingTicks <= 0;
        }

        private void RescheduleLocked()
        {
            _generation++;
            _timer?.Dispose();
            _timer = null;

            if (!_attached || ComputeExpired(out var remainingTicks) || remainingTicks <= 0)
                return;

            // Round up: a timer that fires a little early re-arms rather than misses the expiry.
            var dueMilliseconds = (long)Math.Ceiling(remainingTicks * 1000d / Stopwatch.Frequency);
            dueMilliseconds = Math.Max(1, Math.Min(dueMilliseconds, int.MaxValue - 1));
            _timer = new Timer(_onBudgetElapsed, _generation, dueMilliseconds, Timeout.Infinite);
        }

        private void OnPauseChanged(bool paused)
        {
            Evaluate(-1);
        }

        private void OnBudgetElapsed(object state)
        {
            Evaluate((int)state);
        }

        // generation -1: a pause change; otherwise the timer armed at that generation.
        private void Evaluate(int generation)
        {
            Action handlers = null;
            lock (_gate)
            {
                // Detached, or a timer from an earlier arming: stale.
                if (!_attached || (generation >= 0 && generation != _generation))
                    return;

                RescheduleLocked();
                var expired = ComputeExpired(out _);
                if (expired != _reportedExpired)
                {
                    _reportedExpired = expired;
                    handlers = _expiryChanged;
                }
            }

            handlers?.Invoke();
        }
    }
}
