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
        /// Base URL for resolving relative request URLs.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Default timeout for requests. Defaults to 30 seconds.
        /// Can be overridden per-request via <see cref="UHttpRequestBuilder.WithTimeout"/>.
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
                FollowRedirects = FollowRedirects,
                MaxRedirects = MaxRedirects,
                DisposeTransport = DisposeTransport,
                TlsBackend = TlsBackend
            };
        }
    }
}
