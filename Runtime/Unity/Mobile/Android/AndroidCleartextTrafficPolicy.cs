using System;
using System.Collections.Concurrent;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.Android
{
    /// <summary>
    /// Android player implementation of <see cref="CleartextTrafficPolicy.PlatformPolicy"/>: asks
    /// <c>android.security.NetworkSecurityPolicy.isCleartextTrafficPermitted(host)</c>, which
    /// reflects the app's network security configuration and <c>usesCleartextTraffic</c> (#85).
    /// </summary>
    /// <remarks>
    /// Installed automatically on Android players before the first scene loads, unless the app has
    /// already installed its own policy. Raw sockets are not covered by that configuration, so
    /// TurboHTTP applies it itself. The answer is cached per host (bounded) because the
    /// configuration cannot change while the process runs. The JNI query runs on
    /// <see cref="AndroidJniThread"/>, so the transport thread asking is never attached to the Java
    /// VM (#328). Any JNI failure propagates, and <see cref="CleartextTrafficPolicy"/> treats it as
    /// "not permitted". Needs device validation (#99).
    /// </remarks>
    internal static class AndroidCleartextTrafficPolicy
    {
        private const int MaxCachedHosts = 64;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static readonly ConcurrentDictionary<string, bool> s_cache = NewCache();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            if (CleartextTrafficPolicy.PlatformPolicy == null)
                CleartextTrafficPolicy.PlatformPolicy = IsCleartextTrafficPermitted;
        }
#endif

        /// <summary>
        /// Returns whether the Android network security configuration permits cleartext traffic to
        /// <paramref name="host"/>. Outside Android players it returns <see langword="true"/>
        /// (no platform restriction). Safe on any thread.
        /// </summary>
        internal static bool IsCleartextTrafficPermitted(string host)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Resolve(host, s_cache, AndroidJniThread.Shared, QueryPlatform);
#else
            return true;
#endif
        }

        internal static ConcurrentDictionary<string, bool> NewCache() =>
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Answers from <paramref name="cache"/>, or runs <paramref name="query"/> on
        /// <paramref name="jni"/> and caches its answer. Callers that miss together queue on the JNI
        /// thread, and only the first of them asks Java. A full cache is dropped rather than left
        /// full, so new hosts keep being cached; the configuration is fixed for the process, so
        /// dropping it only costs re-queries.
        /// </summary>
        internal static bool Resolve(
            string host,
            ConcurrentDictionary<string, bool> cache,
            AndroidJniThread jni,
            Func<string, bool> query)
        {
            string normalized = NormalizeHost(host);
            if (cache.TryGetValue(normalized, out bool cached))
                return cached;

            return jni.Invoke(() =>
            {
                if (cache.TryGetValue(normalized, out bool answered))
                    return answered;

                bool permitted = query(normalized);
                if (cache.Count >= MaxCachedHosts)
                    cache.Clear();

                cache.TryAdd(normalized, permitted);
                return permitted;
            });
        }

        private static string NormalizeHost(string host)
        {
            if (string.IsNullOrEmpty(host))
                return string.Empty;

            // Uri.Host keeps the brackets of an IPv6 literal; the Android API takes the bare address.
            return host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']'
                ? host.Substring(1, host.Length - 2)
                : host;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Runs on AndroidJniThread (attached, inside a local-reference frame); the Java objects are
        // released before it returns.
        private static bool QueryPlatform(string host)
        {
            using var version = new UnityEngine.AndroidJavaClass("android.os.Build$VERSION");
            int sdk = version.GetStatic<int>("SDK_INT");

            // NetworkSecurityPolicy exists from API 23; earlier releases have no cleartext policy.
            if (sdk < 23)
                return true;

            using var policyClass = new UnityEngine.AndroidJavaClass("android.security.NetworkSecurityPolicy");
            using var policy = policyClass.CallStatic<UnityEngine.AndroidJavaObject>("getInstance");

            // The per-host overload is API 24+; API 23 only has the app-wide answer.
            return sdk >= 24
                ? policy.Call<bool>("isCleartextTrafficPermitted", host)
                : policy.Call<bool>("isCleartextTrafficPermitted");
        }
#endif
    }
}
