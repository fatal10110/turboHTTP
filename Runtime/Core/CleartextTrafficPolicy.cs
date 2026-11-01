using System;
using System.Threading;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Decides whether a connection may carry cleartext (unencrypted) traffic: an <c>http://</c>
    /// request, a <c>ws://</c> WebSocket, or the leg to an <c>http://</c> proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TurboHTTP opens raw sockets, so neither iOS App Transport Security nor the Android network
    /// security configuration (<c>cleartextTrafficPermitted</c>) is applied by the operating
    /// system. The HTTP transport and the WebSocket transport therefore apply this policy before
    /// they resolve or connect any address.
    /// </para>
    /// <para>
    /// Cleartext is permitted only when the caller's <c>AllowCleartext</c> option is
    /// <see langword="true"/> (<see cref="UHttpClientOptions.AllowCleartext"/>, or the matching
    /// WebSocket connection option) and, when a <see cref="PlatformPolicy"/> is installed, that
    /// policy also permits the host. The option defaults to <see cref="DefaultAllowCleartext"/>:
    /// <see langword="false"/> on iOS and Android players, <see langword="true"/> in the Editor and
    /// on every other target. On Android players the TurboHTTP Unity module installs a platform
    /// policy that asks <c>android.security.NetworkSecurityPolicy</c>.
    /// </para>
    /// </remarks>
    public static class CleartextTrafficPolicy
    {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        private const bool DefaultAllow = false;
#else
        private const bool DefaultAllow = true;
#endif

        private static Func<string, bool> s_platformPolicy;

        /// <summary>
        /// Default value of every <c>AllowCleartext</c> option: <see langword="false"/> on iOS and
        /// Android players, <see langword="true"/> in the Editor and on desktop. A compile-time
        /// constant of the build target; safe on any thread.
        /// </summary>
        public static bool DefaultAllowCleartext => DefaultAllow;

        /// <summary>
        /// Optional platform policy: given a host name, returns whether the operating system's
        /// cleartext policy permits unencrypted traffic to it. <see langword="null"/> (the default
        /// outside Android players) means "no platform restriction". The delegate may run on any
        /// thread and must not block on the main thread; an exception from it refuses the
        /// connection.
        /// </summary>
        public static Func<string, bool> PlatformPolicy
        {
            get => Volatile.Read(ref s_platformPolicy);
            set => Volatile.Write(ref s_platformPolicy, value);
        }

        /// <summary>
        /// Returns whether cleartext traffic to <paramref name="host"/> is permitted, given the
        /// caller's <c>AllowCleartext</c> option and the installed <see cref="PlatformPolicy"/>.
        /// </summary>
        /// <param name="host">Host the cleartext connection would reach (target or proxy).</param>
        /// <param name="allowCleartext">The caller's <c>AllowCleartext</c> option.</param>
        public static bool IsPermitted(string host, bool allowCleartext)
        {
            return IsPermitted(host, allowCleartext, PlatformPolicy);
        }

        internal static bool IsPermitted(string host, bool allowCleartext, Func<string, bool> platformPolicy)
        {
            if (!allowCleartext)
                return false;

            if (platformPolicy == null)
                return true;

            try
            {
                return platformPolicy(host ?? string.Empty);
            }
            catch (Exception)
            {
                // Fail closed: a policy that cannot answer must not open a cleartext connection.
                return false;
            }
        }
    }
}
