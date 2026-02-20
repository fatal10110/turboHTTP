using System;
using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Configuration options for <see cref="UHttpClient"/>.
    /// </summary>
    public class UHttpClientOptions
    {
        /// <summary>
        /// Default maximum decoded HTTP/2 header bytes per header block.
        /// </summary>
        public const int DefaultHttp2MaxDecodedHeaderBytes = 256 * 1024;

        /// <summary>
        /// Base URL for resolving relative request URLs.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Default timeout for requests. Defaults to <see cref="PlatformConfig.RecommendedTimeout"/>.
        /// Can be overridden per-request via <see cref="UHttpRequestBuilder.WithTimeout"/>.
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = PlatformConfig.RecommendedTimeout;

        /// <summary>
        /// Default headers applied to every request.
        /// Request-level headers take precedence over defaults.
        /// </summary>
        public HttpHeaders DefaultHeaders { get; set; } = new HttpHeaders();

        /// <summary>
        /// Transport implementation to use. When null, falls back to
        /// <see cref="HttpTransportFactory.Default"/>.
        /// </summary>
        public IHttpTransport Transport { get; set; }

        /// <summary>
        /// Middleware pipeline components. Stub for Phase 4.
        /// </summary>
        public List<IHttpMiddleware> Middlewares { get; set; } = new List<IHttpMiddleware>();

        /// <summary>
        /// Optional request/response interceptors that run around the middleware pipeline.
        /// </summary>
        public List<IHttpInterceptor> Interceptors { get; set; } = new List<IHttpInterceptor>();

        /// <summary>
        /// How interceptor exceptions are handled.
        /// </summary>
        public InterceptorFailurePolicy InterceptorFailurePolicy { get; set; } = InterceptorFailurePolicy.Propagate;

        /// <summary>
        /// Max time to wait for plugin shutdown callbacks.
        /// </summary>
        public TimeSpan PluginShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Optional adaptive network behavior policy.
        /// </summary>
        public AdaptivePolicy AdaptivePolicy { get; set; } = new AdaptivePolicy();

        /// <summary>
        /// Optional proxy settings (or environment resolution).
        /// </summary>
        public ProxySettings Proxy { get; set; }

        /// <summary>
        /// Optional background networking behavior for mobile-style pause/resume flows.
        /// </summary>
        public BackgroundNetworkingPolicy BackgroundNetworkingPolicy { get; set; } = new BackgroundNetworkingPolicy();

        /// <summary>
        /// Optional platform bridge used by <see cref="BackgroundNetworkingMiddleware"/>
        /// to acquire/release background execution scopes.
        /// </summary>
        public IBackgroundExecutionBridge BackgroundExecutionBridge { get; set; }

        /// <summary>
        /// Detector used by <see cref="AdaptiveMiddleware"/> for quality snapshots.
        /// </summary>
        public NetworkQualityDetector NetworkQualityDetector { get; set; }

        /// <summary>
        /// Whether to follow HTTP redirects automatically.
        /// NOT enforced in Phase 3 — placeholder for Phase 4.
        /// </summary>
        public bool FollowRedirects { get; set; } = true;

        /// <summary>
        /// Maximum number of redirects to follow.
        /// NOT enforced in Phase 3 — placeholder for Phase 4.
        /// </summary>
        public int MaxRedirects { get; set; } = 10;

        /// <summary>
        /// When true and <see cref="Transport"/> is set, the client will dispose
        /// the transport on <see cref="UHttpClient.Dispose()"/>. Has no effect
        /// when using the factory-provided default transport (singleton, never
        /// disposed by clients).
        /// </summary>
        public bool DisposeTransport { get; set; }

        /// <summary>
        /// TLS backend selection strategy.
        /// Default is Auto, which selects the best provider for the current platform.
        /// </summary>
        /// <remarks>
        /// - Auto: Try SslStream first, fall back to BouncyCastle if ALPN unavailable
        /// - SslStream: Force use of System.Net.Security.SslStream (may not support ALPN on all platforms)
        /// - BouncyCastle: Force use of BouncyCastle TLS (guaranteed ALPN support everywhere)
        /// 
        /// Note: Advanced security features (certificate pinning, custom validation callbacks)
        /// are planned for Phase 6 (Advanced Middleware) and not yet available.
        /// </remarks>
        public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;

        /// <summary>
        /// Maximum total decoded HTTP/2 header bytes (name + value) allowed per
        /// header block. Used as decompression-bomb protection for HPACK decoding.
        /// Default is 256KB.
        /// </summary>
        /// <remarks>
        /// This option is applied when <see cref="UHttpClient"/> creates its own
        /// transport instance (default transport path). It does not mutate behavior
        /// of a user-supplied custom <see cref="Transport"/> instance.
        /// </remarks>
        public int Http2MaxDecodedHeaderBytes { get; set; } = DefaultHttp2MaxDecodedHeaderBytes;

        /// <summary>
        /// Creates a deep copy of these options. Headers and middleware list are
        /// cloned; Transport is a shared reference (NOT snapshotted). Middleware
        /// instances are also shared references (typically stateless services —
        /// mutable middleware shared across cloned options may have thread-safety
        /// issues). Users must not mutate or dispose a Transport instance passed
        /// to UHttpClientOptions after constructing a client that uses those options.
        /// </summary>
        public UHttpClientOptions Clone()
        {
            return new UHttpClientOptions
            {
                BaseUrl = BaseUrl,
                DefaultTimeout = DefaultTimeout,
                DefaultHeaders = DefaultHeaders?.Clone() ?? new HttpHeaders(),
                Transport = Transport,
                Middlewares = Middlewares != null ? new List<IHttpMiddleware>(Middlewares) : new List<IHttpMiddleware>(),
                Interceptors = Interceptors != null ? new List<IHttpInterceptor>(Interceptors) : new List<IHttpInterceptor>(),
                InterceptorFailurePolicy = InterceptorFailurePolicy,
                PluginShutdownTimeout = PluginShutdownTimeout,
                AdaptivePolicy = AdaptivePolicy?.Clone() ?? new AdaptivePolicy(),
                Proxy = Proxy?.Clone(),
                BackgroundNetworkingPolicy = BackgroundNetworkingPolicy?.Clone() ?? new BackgroundNetworkingPolicy(),
                BackgroundExecutionBridge = BackgroundExecutionBridge,
                NetworkQualityDetector = NetworkQualityDetector,
                FollowRedirects = FollowRedirects,
                MaxRedirects = MaxRedirects,
                DisposeTransport = DisposeTransport,
                TlsBackend = TlsBackend,
                Http2MaxDecodedHeaderBytes = Http2MaxDecodedHeaderBytes
            };
        }
    }

    public enum InterceptorFailurePolicy
    {
        Propagate,
        ConvertToResponse,
        IgnoreAndContinue
    }
}
