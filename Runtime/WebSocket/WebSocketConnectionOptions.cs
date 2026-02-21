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

        public HttpHeaders CustomHeaders { get; set; } = new HttpHeaders();

        /// <summary>
        /// Optional TLS provider override. Expected runtime type in transport assembly:
        /// TurboHTTP.Transport.Tls.ITlsProvider.
        /// </summary>
        public object TlsProvider { get; set; }

        public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;

        public WebSocketReconnectPolicy ReconnectPolicy { get; set; } = WebSocketReconnectPolicy.None;

        public WebSocketConnectionOptions WithReconnection(WebSocketReconnectPolicy policy)
        {
            ReconnectPolicy = policy ?? WebSocketReconnectPolicy.None;
            return this;
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
                IdleTimeout = IdleTimeout,
                DnsTimeoutMs = DnsTimeoutMs,
                ConnectTimeout = ConnectTimeout,
                SubProtocols = SubProtocols != null ? new List<string>(SubProtocols) : Array.Empty<string>(),
                Extensions = Extensions != null ? new List<string>(Extensions) : Array.Empty<string>(),
                CustomHeaders = CustomHeaders?.Clone() ?? new HttpHeaders(),
                TlsProvider = TlsProvider,
                TlsBackend = TlsBackend,
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
        }
    }
}
