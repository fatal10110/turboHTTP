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
    /// </summary>
    internal static class AndroidAppPauseSource
    {
        private static int _paused;

        internal static event Action<bool> PauseChanged;

        internal static bool IsPaused => Volatile.Read(ref _paused) != 0;

        internal static void Report(bool paused)
        {
            var value = paused ? 1 : 0;
            if (Interlocked.Exchange(ref _paused, value) == value)
                return;

            PauseChanged?.Invoke(paused);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Volatile.Write(ref _paused, 0);
            PauseChanged = null;
        }
    }

    /// <summary>
    /// The default signal: the budget starts at <see cref="AndroidAppPauseSource"/>'s pause
    /// notification and is timed by a <see cref="Timer"/> on the thread pool, which keeps running
    /// while the player loop is paused (#88). Returning to the foreground clears the expiry.
    /// </summary>
    internal sealed class AndroidPauseExpirySignal : IAndroidBackgroundExpirySignal
    {
        private readonly object _gate = new object();
        private readonly int _budgetMilliseconds;
        private readonly Action<bool> _onPauseChanged;
        private readonly TimerCallback _onBudgetElapsed;

        private Timer _timer;
        private bool _paused;
        private bool _expired;
        private long _deadlineTimestamp;
        private int _generation;

        internal AndroidPauseExpirySignal(int budgetMilliseconds)
        {
            _budgetMilliseconds = Math.Max(0, budgetMilliseconds);
            _onPauseChanged = OnPauseChanged;
            _onBudgetElapsed = OnBudgetElapsed;
            AndroidAppPauseSource.PauseChanged += _onPauseChanged;
            if (AndroidAppPauseSource.IsPaused)
                OnPauseChanged(true);
        }

        public event Action ExpiryChanged;

        public bool IsExpired
        {
            get
            {
                lock (_gate)
                {
                    return _expired;
                }
            }
        }

        public TimeSpan RemainingBudget
        {
            get
            {
                lock (_gate)
                {
                    if (_expired)
                        return TimeSpan.Zero;
                    if (!_paused)
                        return TimeSpan.FromMilliseconds(_budgetMilliseconds);

                    var left = _deadlineTimestamp - Stopwatch.GetTimestamp();
                    return left <= 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency);
                }
            }
        }

        internal void OnPauseChanged(bool paused)
        {
            var changed = false;
            lock (_gate)
            {
                if (paused == _paused)
                    return;

                _paused = paused;
                _generation++;
                _timer?.Dispose();
                _timer = null;

                if (paused)
                {
                    _deadlineTimestamp = Stopwatch.GetTimestamp() +
                                         (long)(_budgetMilliseconds * (double)Stopwatch.Frequency / 1000d);
                    if (_budgetMilliseconds == 0)
                    {
                        _expired = true;
                        changed = true;
                    }
                    else
                    {
                        _timer = new Timer(_onBudgetElapsed, _generation, _budgetMilliseconds, Timeout.Infinite);
                    }
                }
                else if (_expired)
                {
                    _expired = false;
                    changed = true;
                }
            }

            if (changed)
                ExpiryChanged?.Invoke();
        }

        private void OnBudgetElapsed(object state)
        {
            lock (_gate)
            {
                // A timer from an earlier pause that fired after a resume is stale.
                if (!_paused || _expired || (int)state != _generation)
                    return;

                _expired = true;
                _timer?.Dispose();
                _timer = null;
            }

            ExpiryChanged?.Invoke();
        }
    }
}
