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
        /// Thresholds and buffer sizes used by buffered and streaming request/response paths.
        /// </summary>
        public StreamingOptions Streaming { get; set; } = new StreamingOptions();

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
        /// Interceptor pipeline components, outermost first: index 0 sees the request first and the
        /// response last. Built-in interceptors contributed from enabled client options
        /// (for example background networking or adaptive behavior) are prepended ahead of this list.
        /// The client rejects orders known to be unsafe with <see cref="System.ArgumentException"/>:
        /// a cache interceptor outside an auth interceptor, or a concurrency interceptor outside a
        /// retry interceptor (canonical order: Documentation~/Modules/Interceptors.md).
        /// </summary>
        public List<IHttpInterceptor> Interceptors { get; set; } = new List<IHttpInterceptor>();

        /// <summary>
        /// Max time to wait for plugin shutdown callbacks.
        /// Used by both <see cref="UHttpClient.UnregisterPluginAsync(string, System.Threading.CancellationToken)"/>
        /// and the best-effort asynchronous shutdown path triggered during <see cref="UHttpClient.Dispose()"/>.
        /// </summary>
        public TimeSpan PluginShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Optional proxy settings (or environment resolution).
        /// </summary>
        public ProxySettings Proxy { get; set; }

        /// <summary>
        /// Whether this client may send cleartext traffic: <c>http://</c> requests and requests
        /// through an <c>http://</c> proxy. Defaults to
        /// <see cref="CleartextTrafficPolicy.DefaultAllowCleartext"/> (<see langword="false"/> on
        /// iOS and Android players, <see langword="true"/> in the Editor and on desktop). When it is
        /// <see langword="false"/>, or the installed <see cref="CleartextTrafficPolicy.PlatformPolicy"/>
        /// refuses the host, the transport throws <see cref="UHttpException"/>
        /// (<see cref="UHttpErrorType.InvalidRequest"/>) before it opens any socket. Raw sockets
        /// are not covered by iOS App Transport Security, so this option is the opt-in for
        /// cleartext on iOS. Proxy Basic credentials additionally require
        /// <see cref="ProxySettings.AllowPlaintextProxyAuth"/>.
        /// </summary>
        public bool AllowCleartext { get; set; } = CleartextTrafficPolicy.DefaultAllowCleartext;

        /// <summary>
        /// Whether to follow HTTP redirects automatically (default <c>true</c>). Applied by
        /// <c>TurboHTTP.Middleware.RedirectInterceptor</c>, which must be in <see cref="Interceptors"/>:
        /// without it a 3xx response is returned to the caller and the client writes a one-time
        /// diagnostic warning. Set it to <c>false</c> to opt out (and silence the warning).
        /// </summary>
        public bool FollowRedirects { get; set; } = true;

        /// <summary>
        /// Maximum number of redirects to follow (default 10). Applied by
        /// <c>TurboHTTP.Middleware.RedirectInterceptor</c>; see <see cref="FollowRedirects"/>.
        /// </summary>
        public int MaxRedirects { get; set; } = 10;

        /// <summary>
        /// Default for <see cref="MaxBufferedResponseBodySize"/>: 100 MB.
        /// </summary>
        public const long DefaultMaxBufferedResponseBodySize = 100L * 1024 * 1024;

        private long _maxBufferedResponseBodySize = DefaultMaxBufferedResponseBodySize;

        /// <summary>
        /// Maximum size, in bytes, of a response body that is collected into memory
        /// (<see cref="UHttpClient.SendBufferedAsync"/> and the other buffered APIs). A larger body fails the
        /// request with a <see cref="UHttpErrorType.NetworkError"/> and the transfer is aborted.
        /// Defaults to 100 MB; 0 means unlimited. Streaming responses
        /// (<see cref="UHttpClient.SendStreamingAsync"/>) are not capped by this option, because their
        /// bytes do not accumulate in memory; <see cref="Http2Options.MaxResponseBodySize"/> is an
        /// optional transport cap for every HTTP/2 response.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public long MaxBufferedResponseBodySize
        {
            get => _maxBufferedResponseBodySize;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be non-negative.");
                _maxBufferedResponseBodySize = value;
            }
        }

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
        /// - Auto: Uses SslStream. Auto never selects BouncyCastle and never switches providers
        ///   after a handshake failure; the error is reported to the caller.
        /// - SslStream: Force use of System.Net.Security.SslStream (may not support ALPN on all platforms)
        /// - BouncyCastle: Force use of BouncyCastle TLS (guaranteed ALPN support everywhere).
        ///   Requires the <c>TURBOHTTP_BOUNCYCASTLE</c> scripting define symbol; without it every
        ///   TLS connection fails with a <see cref="UHttpException"/> whose inner exception is an
        ///   <see cref="System.InvalidOperationException"/> naming the define.
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
                Proxy = Proxy?.Clone(),
                AllowCleartext = AllowCleartext,
                FollowRedirects = FollowRedirects,
                MaxRedirects = MaxRedirects,
                DisposeTransport = DisposeTransport,
                TlsBackend = TlsBackend,
                MaxBufferedResponseBodySize = MaxBufferedResponseBodySize,
                ConnectionPool = ConnectionPool?.Clone() ?? new ConnectionPoolOptions(),
                Http2 = Http2?.Clone() ?? new Http2Options(),
                Streaming = Streaming?.Clone() ?? new StreamingOptions()
            };
        }
    }
}
