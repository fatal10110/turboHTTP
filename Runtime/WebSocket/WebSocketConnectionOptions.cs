using System;
using System.Collections.Generic;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Configuration for a WebSocket connection lifecycle.
    /// </summary>
    public sealed class WebSocketConnectionOptions
    {
        public int MaxFrameSize { get; set; } = WebSocketConstants.DefaultMaxFrameSize;

        public int MaxMessageSize { get; set; } = WebSocketConstants.DefaultMaxMessageSize;

        public int MaxFragmentCount { get; set; } = WebSocketConstants.DefaultMaxFragmentCount;

        public int FragmentationThreshold { get; set; } = WebSocketConstants.DefaultFragmentationThreshold;

        public int ReceiveQueueCapacity { get; set; } = WebSocketConstants.DefaultReceiveQueueCapacity;

        public TimeSpan CloseHandshakeTimeout { get; set; } = WebSocketConstants.DefaultCloseHandshakeTimeout;

        public TimeSpan HandshakeTimeout { get; set; } = WebSocketConstants.DefaultHandshakeTimeout;

        public TimeSpan PingInterval { get; set; } = WebSocketConstants.DefaultPingInterval;

        public TimeSpan PongTimeout { get; set; } = WebSocketConstants.DefaultPongTimeout;

        /// <summary>
        /// Fire metrics update events every N message send/receive operations (0 = disable message-based trigger).
        /// </summary>
        public int MetricsUpdateMessageInterval { get; set; } = 100;

        /// <summary>
        /// Fire metrics update events at least this often (TimeSpan.Zero = disable time-based trigger).
        /// </summary>
        public TimeSpan MetricsUpdateInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Enables connection health diagnostics (RTT windowing, throughput estimate, quality bands).
        /// </summary>
        public bool EnableHealthMonitoring { get; set; }

        /// <summary>
        /// Maximum time without application data frames before considering the socket idle.
        /// Set to <see cref="TimeSpan.Zero"/> to disable.
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// DNS timeout used by transports that resolve hostnames manually.
        /// </summary>
        public int DnsTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// Optional socket connect timeout. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = Timeout.InfiniteTimeSpan;

        public IReadOnlyList<string> SubProtocols { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> Extensions { get; set; } = Array.Empty<string>();

        public IReadOnlyList<Func<IWebSocketExtension>> ExtensionFactories { get; set; } =
            Array.Empty<Func<IWebSocketExtension>>();

        /// <summary>
        /// When true, at least one configured structured extension must be successfully negotiated.
        /// If negotiation fails or none are negotiated, the client fails with close code 1010.
        /// </summary>
        public bool RequireNegotiatedExtensions { get; set; }

        public HttpHeaders CustomHeaders { get; set; } = new HttpHeaders();

        /// <summary>
        /// Optional TLS provider override. Expected runtime type in transport assembly:
        /// TurboHTTP.Transport.Tls.ITlsProvider.
        /// </summary>
        public object TlsProvider { get; set; }

        public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;

        public WebSocketProxySettings ProxySettings { get; set; } = WebSocketProxySettings.None;

        public WebSocketReconnectPolicy ReconnectPolicy { get; set; } = WebSocketReconnectPolicy.None;

        public WebSocketConnectionOptions WithReconnection(WebSocketReconnectPolicy policy)
        {
            ReconnectPolicy = policy ?? WebSocketReconnectPolicy.None;
            return this;
        }

        public WebSocketConnectionOptions WithExtension(Func<IWebSocketExtension> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            var factories = ExtensionFactories != null
                ? new List<Func<IWebSocketExtension>>(ExtensionFactories)
                : new List<Func<IWebSocketExtension>>();
            factories.Add(factory);
            ExtensionFactories = factories;
            return this;
        }

        public WebSocketConnectionOptions WithCompression()
        {
            return WithCompression(PerMessageDeflateOptions.Default);
        }

        public WebSocketConnectionOptions WithRequiredCompression()
        {
            RequireNegotiatedExtensions = true;
            return WithCompression(PerMessageDeflateOptions.Default);
        }

        public WebSocketConnectionOptions WithCompression(PerMessageDeflateOptions options)
        {
            var effectiveOptions = options ?? PerMessageDeflateOptions.Default;
            int maxMessageSize = MaxMessageSize;
            return WithExtension(() => new PerMessageDeflateExtension(effectiveOptions, maxMessageSize));
        }

        public WebSocketConnectionOptions Clone()
        {
            return new WebSocketConnectionOptions
            {
                MaxFrameSize = MaxFrameSize,
                MaxMessageSize = MaxMessageSize,
                MaxFragmentCount = MaxFragmentCount,
                FragmentationThreshold = FragmentationThreshold,
                ReceiveQueueCapacity = ReceiveQueueCapacity,
                CloseHandshakeTimeout = CloseHandshakeTimeout,
                HandshakeTimeout = HandshakeTimeout,
                PingInterval = PingInterval,
                PongTimeout = PongTimeout,
                MetricsUpdateMessageInterval = MetricsUpdateMessageInterval,
                MetricsUpdateInterval = MetricsUpdateInterval,
                EnableHealthMonitoring = EnableHealthMonitoring,
                IdleTimeout = IdleTimeout,
                DnsTimeoutMs = DnsTimeoutMs,
                ConnectTimeout = ConnectTimeout,
                SubProtocols = SubProtocols != null ? new List<string>(SubProtocols) : Array.Empty<string>(),
                Extensions = Extensions != null ? new List<string>(Extensions) : Array.Empty<string>(),
                ExtensionFactories = ExtensionFactories != null
                    ? new List<Func<IWebSocketExtension>>(ExtensionFactories)
                    : Array.Empty<Func<IWebSocketExtension>>(),
                RequireNegotiatedExtensions = RequireNegotiatedExtensions,
                CustomHeaders = CustomHeaders?.Clone() ?? new HttpHeaders(),
                TlsProvider = TlsProvider,
                TlsBackend = TlsBackend,
                ProxySettings = ProxySettings ?? WebSocketProxySettings.None,
                ReconnectPolicy = ReconnectPolicy
            };
        }

        public void Validate()
        {
            if (MaxFrameSize < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxFrameSize), "Must be > 0.");
            if (MaxMessageSize < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxMessageSize), "Must be > 0.");
            if (MaxFragmentCount < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxFragmentCount), "Must be > 0.");
            if (FragmentationThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(FragmentationThreshold), "Must be > 0.");
            if (ReceiveQueueCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(ReceiveQueueCapacity), "Must be > 0.");
            if (DnsTimeoutMs < 1)
                throw new ArgumentOutOfRangeException(nameof(DnsTimeoutMs), "Must be > 0.");
            if (CloseHandshakeTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(CloseHandshakeTimeout), "Must be >= 0.");
            if (HandshakeTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), "Must be > 0.");
            if (PingInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(PingInterval), "Must be >= 0.");
            if (PongTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(PongTimeout), "Must be >= 0.");
            if (MetricsUpdateMessageInterval < 0)
                throw new ArgumentOutOfRangeException(nameof(MetricsUpdateMessageInterval), "Must be >= 0.");
            if (MetricsUpdateInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(MetricsUpdateInterval), "Must be >= 0.");
            if (IdleTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(IdleTimeout), "Must be >= 0.");

            if (ConnectTimeout != Timeout.InfiniteTimeSpan && ConnectTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "Must be >= 0 or Timeout.InfiniteTimeSpan.");

            if (FragmentationThreshold > MaxFrameSize)
                throw new ArgumentException("FragmentationThreshold must be <= MaxFrameSize.");

            long maxMessageByFragments = (long)MaxFrameSize * MaxFragmentCount;
            if (MaxMessageSize > maxMessageByFragments)
                throw new ArgumentException("MaxMessageSize must be <= MaxFrameSize * MaxFragmentCount.");

            if (ReconnectPolicy == null)
                throw new ArgumentNullException(nameof(ReconnectPolicy), "Reconnect policy cannot be null.");

            if (ProxySettings == null)
                throw new ArgumentNullException(nameof(ProxySettings), "ProxySettings cannot be null.");

            if (SubProtocols != null)
            {
                for (int i = 0; i < SubProtocols.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(SubProtocols[i]))
                        continue;

                    string token = SubProtocols[i].Trim();
                    if (!IsHeaderToken(token))
                    {
                        throw new ArgumentException(
                            "Sub-protocol entry is not a valid HTTP token: " + token,
                            nameof(SubProtocols));
                    }
                }
            }

            if (ExtensionFactories != null)
            {
                for (int i = 0; i < ExtensionFactories.Count; i++)
                {
                    if (ExtensionFactories[i] == null)
                    {
                        throw new ArgumentException(
                            "ExtensionFactories must not contain null entries.",
                            nameof(ExtensionFactories));
                    }
                }
            }

            if (RequireNegotiatedExtensions &&
                (ExtensionFactories == null || ExtensionFactories.Count == 0))
            {
                throw new ArgumentException(
                    "RequireNegotiatedExtensions requires at least one ExtensionFactories entry.");
            }
        }

        private static bool IsHeaderToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c <= 32 || c >= 127)
                    return false;

                switch (c)
                {
                    case '(': case ')': case '<': case '>': case '@':
                    case ',': case ';': case ':': case '\\': case '"':
                    case '/': case '[': case ']': case '?': case '=':
                    case '{': case '}':
                        return false;
                }
            }

            return true;
        }
    }
}
