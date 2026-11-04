using System;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Selects the socket I/O implementation used by the connection pool.
    /// </summary>
    public enum SocketIoMode
    {
        /// <summary>
        /// Default mode. I/O is performed via <see cref="System.Net.Sockets.NetworkStream"/>
        /// and the BCL async infrastructure. Compatible with all supported platforms.
        /// </summary>
        NetworkStream = 0,

        /// <summary>
        /// Opt-in high-performance mode. I/O is performed via
        /// <see cref="System.Net.Sockets.SocketAsyncEventArgs"/> (SAEA), avoiding per-operation
        /// <see cref="System.Threading.Tasks.Task"/> allocations on the hot receive path.
        /// Uses IOCP on Windows, kqueue on macOS/iOS, and epoll on Android/Linux.
        /// Not available on WebGL (Transport assembly is excluded from WebGL builds).
        /// </summary>
        /// <remarks>
        /// SAEA relies on the runtime's async I/O completion ports. Under IL2CPP on some
        /// consoles and mobile targets, the translated async callback paths have historically
        /// been prone to edge-case bugs, memory leaks, or thread deadlocks. Consider
        /// <see cref="PollSelect"/> as a more stable alternative on those platforms.
        /// </remarks>
        Saea = 1,

        /// <summary>
        /// Custom synchronous non-blocking I/O mode using <see cref="System.Net.Sockets.Socket.Poll"/>
        /// and <see cref="System.Net.Sockets.Socket.Select"/>. Avoids the runtime's async thread pool
        /// entirely, making it significantly more stable across IL2CPP platforms (consoles,
        /// mobile, and any target where SAEA/async callbacks are unreliable).
        /// </summary>
        /// <remarks>
        /// This mode runs one dedicated poll thread per connection, reading/writing synchronously
        /// when the socket is ready. The thread blocks on a signal while no operation is pending,
        /// so an idle connection costs no CPU; while a receive is pending it polls in 20 ms slices
        /// (about 50 wake-ups per second). Safer than SAEA on PlayStation, Xbox, Nintendo Switch,
        /// Android, and iOS under IL2CPP.
        /// </remarks>
        PollSelect = 2,
    }

    /// <summary>
    /// Configuration options for the TCP connection pool used by the default transport.
    /// </summary>
    public sealed class ConnectionPoolOptions
    {
        /// <summary>Default <see cref="ConnectTimeout"/>: 10 seconds.</summary>
        internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

        private int _maxConnectionsPerHost = PlatformConfig.RecommendedMaxConcurrency;
        private TimeSpan _connectionIdleTimeout = PlatformConfig.RecommendedConnectionIdleTimeout;
        private TimeSpan _reusedConnectionFirstByteTimeout = PlatformConfig.DefaultReusedConnectionFirstByteTimeout;
        private int _dnsTimeoutMs = 10000;
        private TimeSpan _connectTimeout = DefaultConnectTimeout;
        private HappyEyeballsOptions _happyEyeballs = new HappyEyeballsOptions();
        private SocketIoMode _socketIoMode = SocketIoMode.NetworkStream;

        // Set by every setter, even when it assigns the default value (#184), so IsDefault()
        // means "never configured", not "equal to the defaults".
        private bool _explicitlySet;

        /// <summary>
        /// Maximum concurrent connections per host. Defaults to platform recommendation
        /// (e.g., 6 on Desktop, 8 on Mobile).
        /// </summary>
        public int MaxConnectionsPerHost
        {
            get => _maxConnectionsPerHost;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be greater than 0.");
                _maxConnectionsPerHost = value;
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// How long idle connections remain pooled before being closed. Defaults to 45 seconds on
        /// iOS/Android players and 2 minutes in the Editor and on desktop: mobile NATs drop idle
        /// connections sooner.
        /// </summary>
        public TimeSpan ConnectionIdleTimeout
        {
            get => _connectionIdleTimeout;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be positive.");
                _connectionIdleTimeout = value;
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// Timeout for DNS resolutions. Defaults to 10 seconds.
        /// </summary>
        public int DnsTimeoutMs
        {
            get => _dnsTimeoutMs;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be greater than 0.");
                _dnsTimeoutMs = value;
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// Wall-clock deadline for establishing the TCP connection to a host: every address
        /// attempt of one connect, after DNS and before TLS. Defaults to 10 seconds.
        /// </summary>
        /// <remarks>
        /// The request timeout (<c>UHttpRequest.Timeout</c>) still bounds the connect as well, so the
        /// effective limit is the smaller of the two. When this deadline expires the request fails
        /// with <c>UHttpErrorType.Timeout</c>. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
        /// disables it (the request timeout then remains the only bound). Values must be positive
        /// and at most <see cref="int.MaxValue"/> milliseconds.
        /// </remarks>
        public TimeSpan ConnectTimeout
        {
            get => _connectTimeout;
            set
            {
                if (value != System.Threading.Timeout.InfiniteTimeSpan)
                {
                    if (value <= TimeSpan.Zero)
                        throw new ArgumentOutOfRangeException(nameof(value), "Must be positive or Timeout.InfiniteTimeSpan.");
                    if (value.TotalMilliseconds > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(value),
                            "Must be less than or equal to Int32.MaxValue milliseconds.");
                }

                _connectTimeout = value;
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// How long a <b>reused</b> HTTP/1.1 connection may stay silent after an idempotent request
        /// was sent before it is treated as dead. Defaults to 10 seconds.
        /// </summary>
        /// <remarks>
        /// A pooled connection can be half-dead (the peer or a NAT dropped it without a FIN or RST).
        /// When no response byte arrives within this time, the connection is closed and the request
        /// is sent once more on a fresh connection, like a connection the server closed while idle
        /// (RFC 9112 section 9.3.1). It applies only to idempotent methods with a replayable body
        /// (RFC 9110 section 9.2.2) on a reused connection; a POST, a fresh connection and HTTP/2
        /// keep waiting until the request timeout. The request timeout still bounds the whole
        /// request. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it. Values must
        /// be positive and at most <see cref="int.MaxValue"/> milliseconds.
        /// </remarks>
        public TimeSpan ReusedConnectionFirstByteTimeout
        {
            get => _reusedConnectionFirstByteTimeout;
            set
            {
                if (value != System.Threading.Timeout.InfiniteTimeSpan)
                {
                    if (value <= TimeSpan.Zero)
                        throw new ArgumentOutOfRangeException(nameof(value), "Must be positive or Timeout.InfiniteTimeSpan.");
                    if (value.TotalMilliseconds > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(value),
                            "Must be less than or equal to Int32.MaxValue milliseconds.");
                }

                _reusedConnectionFirstByteTimeout = value;
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// Options for Happy Eyeballs (RFC 8305) dual-stack IPv4/IPv6 connection racing.
        /// </summary>
        public HappyEyeballsOptions HappyEyeballs
        {
            get => _happyEyeballs;
            set
            {
                _happyEyeballs = value ?? throw new ArgumentNullException(nameof(value));
                _explicitlySet = true;
            }
        }

        /// <summary>
        /// Socket I/O implementation to use for send and receive operations.
        /// Defaults to <see cref="SocketIoMode.NetworkStream"/> for maximum compatibility.
        /// Set to <see cref="SocketIoMode.Saea"/> to enable zero-allocation socket I/O on
        /// platforms with native async socket support (IOCP/kqueue/epoll).
        /// </summary>
        public SocketIoMode SocketIoMode
        {
            get => _socketIoMode;
            set
            {
                if (value != SocketIoMode.NetworkStream && value != SocketIoMode.Saea && value != SocketIoMode.PollSelect)
                    throw new ArgumentOutOfRangeException(nameof(value), "Unknown SocketIoMode value.");
                _socketIoMode = value;
                _explicitlySet = true;
            }
        }

        /// <summary>Returns an independent copy, including whether each option was configured.</summary>
        public ConnectionPoolOptions Clone()
        {
            return new ConnectionPoolOptions
            {
                _maxConnectionsPerHost = _maxConnectionsPerHost,
                _connectionIdleTimeout = _connectionIdleTimeout,
                _reusedConnectionFirstByteTimeout = _reusedConnectionFirstByteTimeout,
                _dnsTimeoutMs = _dnsTimeoutMs,
                _connectTimeout = _connectTimeout,
                _happyEyeballs = _happyEyeballs.Clone(),
                _socketIoMode = _socketIoMode,
                _explicitlySet = _explicitlySet,
            };
        }

        /// <summary>
        /// True when no option (including a nested <see cref="HappyEyeballs"/> option) was ever
        /// assigned. Assigning a value equal to its default still counts as configured, so the
        /// client then builds a dedicated transport from these options.
        /// </summary>
        internal bool IsDefault()
        {
            return !_explicitlySet && _happyEyeballs.IsDefault();
        }
    }
}
