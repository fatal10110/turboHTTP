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
        /// This mode runs a tight poll loop on a dedicated thread per connection (or a
        /// multiplexed select loop), reading/writing synchronously when data is available.
        /// Slightly higher CPU usage than SAEA on desktop, but much safer on PlayStation,
        /// Xbox, Nintendo Switch, Android, and iOS under IL2CPP.
        /// </remarks>
        PollSelect = 2,
    }

    /// <summary>
    /// Configuration options for the TCP connection pool used by the default transport.
    /// </summary>
    public sealed class ConnectionPoolOptions
    {
        private int _maxConnectionsPerHost = PlatformConfig.RecommendedMaxConcurrency;
        private TimeSpan _connectionIdleTimeout = TimeSpan.FromMinutes(2);
        private int _dnsTimeoutMs = 10000;
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
                _happyEyeballs = _happyEyeballs.Clone(),
                _socketIoMode = _socketIoMode,
            };
        }

        internal bool IsDefault()
        {
            return _maxConnectionsPerHost == PlatformConfig.RecommendedMaxConcurrency &&
                   _connectionIdleTimeout == TimeSpan.FromMinutes(2) &&
                   _dnsTimeoutMs == 10000 &&
                   _happyEyeballs.IsDefault() &&
                   _socketIoMode == SocketIoMode.NetworkStream;
        }
    }
}
