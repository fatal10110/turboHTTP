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
        private TimeSpan _connectionIdleTimeout = TimeSpan.FromMinutes(2);
        private int _dnsTimeoutMs = 10000;
        private TimeSpan _connectTimeout = DefaultConnectTimeout;
        private HappyEyeballsOptions _happyEyeballs = new HappyEyeballsOptions();
        private SocketIoMode _socketIoMode = SocketIoMode.NetworkStream;

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
            }
        }

        /// <summary>
        /// How long idle connections remain pooled before being closed. Defaults to 2 minutes.
        /// </summary>
        public TimeSpan ConnectionIdleTimeout
        {
            get => _connectionIdleTimeout;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be positive.");
                _connectionIdleTimeout = value;
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
            }
        }

        /// <summary>
        /// Options for Happy Eyeballs (RFC 8305) dual-stack IPv4/IPv6 connection racing.
        /// </summary>
        public HappyEyeballsOptions HappyEyeballs
        {
            get => _happyEyeballs;
            set => _happyEyeballs = value ?? throw new ArgumentNullException(nameof(value));
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
            }
        }

        public ConnectionPoolOptions Clone()
        {
            return new ConnectionPoolOptions
            {
                _maxConnectionsPerHost = _maxConnectionsPerHost,
                _connectionIdleTimeout = _connectionIdleTimeout,
                _dnsTimeoutMs = _dnsTimeoutMs,
                _connectTimeout = _connectTimeout,
                _happyEyeballs = _happyEyeballs.Clone(),
                _socketIoMode = _socketIoMode,
            };
        }

        internal bool IsDefault()
        {
            return _maxConnectionsPerHost == PlatformConfig.RecommendedMaxConcurrency &&
                   _connectionIdleTimeout == TimeSpan.FromMinutes(2) &&
                   _dnsTimeoutMs == 10000 &&
                   _connectTimeout == DefaultConnectTimeout &&
                   _happyEyeballs.IsDefault() &&
                   _socketIoMode == SocketIoMode.NetworkStream;
        }
    }
}
