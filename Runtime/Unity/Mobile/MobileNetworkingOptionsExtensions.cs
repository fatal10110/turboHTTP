using System;
using System.Collections.Generic;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile
{
    /// <summary>
    /// Adds the mobile networking interceptors (adaptive timeouts and background pause/replay) to
    /// <see cref="UHttpClientOptions.Interceptors"/>. These features live in <c>TurboHTTP.Unity</c>,
    /// not in Core: a client that does not call these methods carries none of them.
    /// </summary>
    /// <remarks>
    /// Both methods insert at the front of the interceptor list so the canonical order is
    /// <see cref="AdaptiveInterceptor"/>, then <see cref="BackgroundNetworkingInterceptor"/>, then
    /// the caller's interceptors, whatever order the two methods are called in. Call them on the
    /// options before passing them to <see cref="UHttpClient"/>; replacing
    /// <see cref="UHttpClientOptions.Interceptors"/> afterwards drops them.
    /// </remarks>
    public static class MobileNetworkingOptionsExtensions
    {
        /// <summary>
        /// Inserts an <see cref="AdaptiveInterceptor"/> that scales request timeouts from recent
        /// network quality samples.
        /// </summary>
        /// <param name="options">Options to configure.</param>
        /// <param name="policy">Policy to apply (cloned). <see langword="null"/> uses a default
        /// policy with <see cref="AdaptivePolicy.Enable"/> set to <see langword="true"/>. The
        /// interceptor passes requests through unchanged while the policy is disabled.</param>
        /// <param name="detector">Quality detector shared with the caller, or <see langword="null"/>
        /// for a new one.</param>
        /// <returns>The inserted interceptor, or the <see cref="AdaptiveInterceptor"/> already in
        /// the list (nothing is inserted then).</returns>
        public static AdaptiveInterceptor UseAdaptiveNetworking(
            this UHttpClientOptions options,
            AdaptivePolicy policy = null,
            NetworkQualityDetector detector = null)
        {
            var interceptors = GetInterceptors(options);
            var existing = Find<AdaptiveInterceptor>(interceptors);
            if (existing != null)
                return existing;

            var interceptor = new AdaptiveInterceptor(
                policy?.Clone() ?? new AdaptivePolicy { Enable = true },
                detector ?? new NetworkQualityDetector());
            interceptors.Insert(0, interceptor);
            return interceptor;
        }

        /// <summary>
        /// Inserts a <see cref="BackgroundNetworkingInterceptor"/> that holds background execution
        /// scopes while requests run and queues expired requests for replay.
        /// </summary>
        /// <param name="options">Options to configure.</param>
        /// <param name="policy">Policy to apply (cloned). <see langword="null"/> uses a default
        /// policy with <see cref="BackgroundNetworkingPolicy.Enable"/> set to
        /// <see langword="true"/>.</param>
        /// <param name="bridge">Platform bridge, or <see langword="null"/> for the platform default
        /// (iOS background task or Android deferred work in players; none in the Editor and on
        /// desktop).</param>
        /// <returns>The inserted interceptor, or the <see cref="BackgroundNetworkingInterceptor"/>
        /// already in the list (nothing is inserted then).</returns>
        public static BackgroundNetworkingInterceptor UseBackgroundNetworking(
            this UHttpClientOptions options,
            BackgroundNetworkingPolicy policy = null,
            IBackgroundExecutionBridge bridge = null)
        {
            var interceptors = GetInterceptors(options);
            var existing = Find<BackgroundNetworkingInterceptor>(interceptors);
            if (existing != null)
                return existing;

            var interceptor = new BackgroundNetworkingInterceptor(
                policy?.Clone() ?? new BackgroundNetworkingPolicy { Enable = true },
                bridge ?? BackgroundExecutionBridgeFactory.CreateDefault());
            var index = interceptors.Count > 0 && interceptors[0] is AdaptiveInterceptor ? 1 : 0;
            interceptors.Insert(index, interceptor);
            return interceptor;
        }

        private static List<IHttpInterceptor> GetInterceptors(UHttpClientOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            return options.Interceptors ??= new List<IHttpInterceptor>();
        }

        private static T Find<T>(List<IHttpInterceptor> interceptors) where T : class
        {
            for (int i = 0; i < interceptors.Count; i++)
            {
                if (interceptors[i] is T match)
                    return match;
            }

            return null;
        }
    }
}
