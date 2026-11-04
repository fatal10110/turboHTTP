namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Calls the bridge needs from <c>com.turbohttp.background.TurboHttpBackgroundPlugin</c>. Every
    /// method runs synchronously on the calling thread. Implemented over JNI by
    /// <see cref="AndroidBackgroundPluginJni"/>, and by fakes in tests.
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
    /// JNI implementation, compiled only into Android players built with <c>TURBOHTTP_BACKGROUND_NETWORKING</c>
    /// (the same define constraint that includes the .androidlib, so the Java class is always present).
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
            return string.IsNullOrEmpty(className) ? null : new AndroidBackgroundPluginJni(className);
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

                AttachCurrentThread();
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
            AttachCurrentThread();
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic("beginInProcessGuard", scopeId, graceMilliseconds);
            return true;
        }

        public void EndGuard(string scopeId)
        {
            AttachCurrentThread();
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic("endInProcessGuard", scopeId);
        }

        public bool EnqueueDeferredWork(string dedupeKey)
        {
            AttachCurrentThread();
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            return plugin.CallStatic<bool>("enqueueDeferredWork", dedupeKey);
        }

        public void CancelDeferredWork(string dedupeKey)
        {
            if (System.Threading.Volatile.Read(ref _initialized) == 0)
                return;

            AttachCurrentThread();
            using var plugin = new UnityEngine.AndroidJavaClass(_className);
            plugin.CallStatic<bool>("cancelDeferredWork", dedupeKey);
        }

        private static void AttachCurrentThread()
        {
            // JNI from a thread-pool thread needs the thread attached to the VM; a no-op when it already
            // is (the main thread, or a worker attached by an earlier call). Pool threads stay attached.
            UnityEngine.AndroidJNI.AttachCurrentThread();
        }
#endif
    }
}
