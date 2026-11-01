using System;
using System.Runtime.InteropServices;

namespace TurboHTTP.Unity.Mobile.iOS
{
    /// <summary>
    /// Native calls the iOS background bridge needs. Implemented by <see cref="IosBackgroundTaskBindings"/>
    /// in iOS players built with <c>TURBOHTTP_BACKGROUND_NETWORKING</c>, and by fakes in tests.
    /// </summary>
    internal interface IIosBackgroundTaskNative
    {
        /// <summary>
        /// Begins a UIKit background task for <paramref name="cookie"/>; any thread. When iOS is about to
        /// suspend the app it calls <see cref="IosBackgroundTaskBridge.HandleNativeExpiration"/> with the
        /// cookie and ends the task itself.
        /// </summary>
        bool Begin(string taskName, int cookie);

        /// <summary>Ends the task begun for <paramref name="cookie"/> if it is still running; any thread.</summary>
        void End(int cookie);

        /// <summary>UIApplication.backgroundTimeRemaining in seconds; any negative value means unlimited.</summary>
        double GetRemainingSeconds();
    }

    internal sealed class IosBackgroundTaskBindings
#if UNITY_IOS && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
        : IIosBackgroundTaskNative
#endif
    {
        /// <summary>
        /// Remaining background time above this many seconds means "no limit": UIKit reports DBL_MAX while
        /// the app is in the foreground, and real background budgets are minutes at most (#87).
        /// </summary>
        internal const double UnlimitedThresholdSeconds = 1.0e6;

        /// <summary>
        /// The native bindings in iOS players built with <c>TURBOHTTP_BACKGROUND_NETWORKING</c>; otherwise
        /// <see langword="null"/>, and the bridge acquires no scope (foreground semantics).
        /// </summary>
        internal static IIosBackgroundTaskNative Platform
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
                return Instance;
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// Converts a native remaining-time reading to a budget. Foreground (DBL_MAX), infinity, NaN,
        /// the native "unlimited" sentinel (negative) and anything above <see cref="UnlimitedThresholdSeconds"/>
        /// map to <see cref="TimeSpan.MaxValue"/>: a reading that cannot be represented never becomes
        /// <see cref="TimeSpan.Zero"/>, which would read as "expired" (#87).
        /// </summary>
        internal static TimeSpan ToRemainingBudget(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0 || seconds > UnlimitedThresholdSeconds)
                return TimeSpan.MaxValue;

            return TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
        }

#if UNITY_IOS && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
        // Held for the process lifetime so the native side never calls a collected delegate.
        private static readonly ExpiredCallback ExpiredCallbackDelegate = OnExpired;

        private static readonly IosBackgroundTaskBindings Instance = new IosBackgroundTaskBindings();

        private static readonly object CallbackLock = new object();

        // 1 once the expiration callback is registered with the native side; written under CallbackLock.
        private static int s_callbackRegistered;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ExpiredCallback(int cookie);

        [DllImport("__Internal")]
        private static extern void turbohttp_set_background_expiration_callback(ExpiredCallback callback);

        [DllImport("__Internal")]
        private static extern int turbohttp_begin_background_task(string taskName, int cookie);

        [DllImport("__Internal")]
        private static extern void turbohttp_end_background_task(int cookie);

        [DllImport("__Internal")]
        private static extern double turbohttp_background_time_remaining();

        private IosBackgroundTaskBindings()
        {
        }

        // Registered lazily from Begin (the bridge catches a missing native library there, so nothing
        // native runs while the type initializes). The lock makes every begin wait until the callback
        // is in place, so no task can expire without signalling its scope.
        private static void EnsureCallbackRegistered()
        {
            if (System.Threading.Volatile.Read(ref s_callbackRegistered) != 0)
                return;

            lock (CallbackLock)
            {
                if (s_callbackRegistered != 0)
                    return;

                turbohttp_set_background_expiration_callback(ExpiredCallbackDelegate);
                System.Threading.Volatile.Write(ref s_callbackRegistered, 1);
            }
        }

        [AOT.MonoPInvokeCallback(typeof(ExpiredCallback))]
        private static void OnExpired(int cookie)
        {
            // Called by UIKit's expiration handler on the main thread; must never throw into native code.
            try
            {
                IosBackgroundTaskBridge.HandleNativeExpiration(cookie);
            }
            catch
            {
                // Nothing to report to native code; the task is ended by the caller either way.
            }
        }

        public bool Begin(string taskName, int cookie)
        {
            EnsureCallbackRegistered();
            return turbohttp_begin_background_task(taskName, cookie) != 0;
        }

        public void End(int cookie)
        {
            turbohttp_end_background_task(cookie);
        }

        public double GetRemainingSeconds()
        {
            return turbohttp_background_time_remaining();
        }
#endif
    }
}
