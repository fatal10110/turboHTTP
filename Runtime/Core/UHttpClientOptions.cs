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
        /// Configuration for the TCP connection pool used by the default transport.
        /// </summary>
        public ConnectionPoolOptions ConnectionPool { get; set; } = new ConnectionPoolOptions();

        /// <summary>
        /// HTTP/2 specific client settings.
        /// </summary>
        public Http2Options Http2 { get; set; } = new Http2Options();

        /// <summary>
        /// Base URL for resolving relative request URLs.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Default timeout for requests. Defaults to <see cref="PlatformConfig.RecommendedTimeout"/>.
        /// Can be overridden per-request via <see cref="UHttpRequest.WithTimeout(TimeSpan)"/>.
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
        /// Interceptor pipeline components.
        /// Built-in interceptors contributed from enabled client options
        /// (for example background networking or adaptive behavior) are prepended ahead of this list.
        /// </summary>
        public List<IHttpInterceptor> Interceptors { get; set; } = new List<IHttpInterceptor>();

        /// <summary>
        /// Max time to wait for plugin shutdown callbacks.
        /// Used by both <see cref="UHttpClient.UnregisterPluginAsync(string, System.Threading.CancellationToken)"/>
        /// and the best-effort asynchronous shutdown path triggered during <see cref="UHttpClient.Dispose()"/>.
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
        /// Optional platform bridge used by <see cref="BackgroundNetworkingInterceptor"/>
        /// to acquire/release background execution scopes.
        /// </summary>
        public IBackgroundExecutionBridge BackgroundExecutionBridge { get; set; }

        /// <summary>
        /// Detector used by <see cref="AdaptiveInterceptor"/> for quality snapshots.
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
        /// - Auto: Prefer SslStream; use BouncyCastle only if platform TLS is unavailable.
        ///   The first TLS handshake probes SslStream viability; failures are cached process-wide.
        /// - SslStream: Force use of System.Net.Security.SslStream (may not support ALPN on all platforms)
        /// - BouncyCastle: Force use of BouncyCastle TLS (guaranteed ALPN support everywhere)
        /// 
        /// Note: Advanced security features (certificate pinning, custom validation callbacks)
        /// are planned for Phase 6 (Advanced Middleware) and not yet available.
        /// </remarks>
        public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;

        /// <summary>
        /// Creates a deep copy of these options. Headers and pipeline component lists are
        /// cloned; Transport is a shared reference (NOT snapshotted). Interceptor
        /// instances are shared references. Stateful interceptors are therefore
        /// shared across option clones by design.
        /// </summary>
        public UHttpClientOptions Clone()
        {
            return new UHttpClientOptions
            {
                BaseUrl = BaseUrl,
                DefaultTimeout = DefaultTimeout,
                DefaultHeaders = DefaultHeaders?.Clone() ?? new HttpHeaders(),
                Transport = Transport,
                Interceptors = Interceptors != null ? new List<IHttpInterceptor>(Interceptors) : new List<IHttpInterceptor>(),
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
                ConnectionPool = ConnectionPool?.Clone() ?? new ConnectionPoolOptions(),
                Http2 = Http2?.Clone() ?? new Http2Options()
            };
        }
    }
}
