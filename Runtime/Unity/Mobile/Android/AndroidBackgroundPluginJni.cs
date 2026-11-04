namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Calls the bridge needs from <c>com.turbohttp.background.TurboHttpBackgroundPlugin</c>. Every
    /// method is synchronous: it returns once the plugin has handled the call. Implemented over JNI by
    /// <see cref="AndroidBackgroundPluginJni"/> (run through <see cref="AndroidBackgroundPluginOnJniThread"/>),
    /// and by fakes in tests.
    /// </summary>
    internal interface IAndroidBackgroundPlugin
    {
        /// <summary>Hands the application context to the plugin once; <see langword="false"/> while unavailable.</summary>
        bool EnsureInitialized();

        bool BeginGuard(string scopeId, int graceMilliseconds);

        void EndGuard(string scopeId);

        bool EnqueueDeferredWork(string dedupeKey);

        void CancelDeferredWork(string dedupeKey);
    }

    /// <summary>
    /// Runs every call of an inner plugin on <see cref="AndroidJniThread"/>, so the bridge's
    /// thread-pool callers are never attached to the Java VM (#328). The Unity main thread runs the
    /// call inline.
    /// </summary>
    internal sealed class AndroidBackgroundPluginOnJniThread : IAndroidBackgroundPlugin
    {
        private readonly IAndroidBackgroundPlugin _inner;
        private readonly AndroidJniThread _jni;

        internal AndroidBackgroundPluginOnJniThread(IAndroidBackgroundPlugin inner, AndroidJniThread jni)
        {
            _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
            _jni = jni ?? throw new System.ArgumentNullException(nameof(jni));
        }

        public bool EnsureInitialized() => _jni.Invoke(_inner.EnsureInitialized);

        public bool BeginGuard(string scopeId, int graceMilliseconds) =>
            _jni.Invoke(() => _inner.BeginGuard(scopeId, graceMilliseconds));

        public void EndGuard(string scopeId) => _jni.Invoke(() =>
        {
            _inner.EndGuard(scopeId);
            return true;
        });

        public bool EnqueueDeferredWork(string dedupeKey) =>
            _jni.Invoke(() => _inner.EnqueueDeferredWork(dedupeKey));

        public void CancelDeferredWork(string dedupeKey) => _jni.Invoke(() =>
        {
            _inner.CancelDeferredWork(dedupeKey);
            return true;
        });
    }

    /// <summary>
    /// JNI implementation, compiled only into Android players built with <c>TURBOHTTP_BACKGROUND_NETWORKING</c>
    /// (the same define constraint that includes the .androidlib, so the Java class is always present).
    /// It assumes an attached thread: <see cref="Create"/> wraps it in
    /// <see cref="AndroidBackgroundPluginOnJniThread"/>, and nothing calls it directly.
    /// </summary>
    internal sealed class AndroidBackgroundPluginJni
#if UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
        : IAndroidBackgroundPlugin
#endif
    {
        /// <summary>The JNI plugin in opted-in Android players; otherwise <see langword="null"/>.</summary>
        internal static IAndroidBackgroundPlugin Create(string className)
        {
#if UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
            return string.IsNullOrEmpty(className)
                ? null
                : new AndroidBackgroundPluginOnJniThread(new AndroidBackgroundPluginJni(className), AndroidJniThread.Shared);
#else
            return null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR && TURBOHTTP_BACKGROUND_NETWORKING
        private readonly string _className;
        private readonly object _initLock = new object();
        private int _initialized;

        private AndroidBackgroundPluginJni(string className)
        {
            _className = className;
        }

        public bool EnsureInitialized()
        {
            if (System.Threading.Volatile.Read(ref _initialized) != 0)
                return true;

            lock (_initLock)
            {
                if (_initialized != 0)
                    return true;

                using var plugin = new UnityEngine.AndroidJavaClass(_className);
                using var unityPlayer = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity");
                if (activity == null || !plugin.CallStatic<bool>("initialize", activity))
                    return false;

                System.Threading.Volatile.Write(ref _initialized, 1);
                return true;
            }
        }

        public bool BeginGuard(string scopeId, int graceMilliseconds)
        {
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic("beginInProcessGuard", scopeId, graceMilliseconds);
            return true;
        }

        public void EndGuard(string scopeId)
        {
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic("endInProcessGuard", scopeId);
        }

        public bool EnqueueDeferredWork(string dedupeKey)
        {
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            return plugin.CallStatic<bool>("enqueueDeferredWork", dedupeKey);
        }

        public void CancelDeferredWork(string dedupeKey)
        {
            if (System.Threading.Volatile.Read(ref _initialized) == 0)
                return;

            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic<bool>("cancelDeferredWork", dedupeKey);
        }
#endif
    }
}
