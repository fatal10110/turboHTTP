using System;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Configuration options for the TCP connection pool used by the default transport.
    /// </summary>
    public sealed class ConnectionPoolOptions
    {
        private int _maxConnectionsPerHost = PlatformConfig.RecommendedMaxConcurrency;
        private TimeSpan _connectionIdleTimeout = TimeSpan.FromMinutes(2);
        private int _dnsTimeoutMs = 10000;
        private HappyEyeballsOptions _happyEyeballs = new HappyEyeballsOptions();

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

        public ConnectionPoolOptions Clone()
        {
            return new ConnectionPoolOptions
            {
                _maxConnectionsPerHost = _maxConnectionsPerHost,
                _connectionIdleTimeout = _connectionIdleTimeout,
                _dnsTimeoutMs = _dnsTimeoutMs,
                _happyEyeballs = _happyEyeballs.Clone()
            };
        }

        internal bool IsDefault()
        {
            return _maxConnectionsPerHost == PlatformConfig.RecommendedMaxConcurrency &&
                   _connectionIdleTimeout == TimeSpan.FromMinutes(2) &&
                   _dnsTimeoutMs == 10000 &&
                   _happyEyeballs.IsDefault();
        }
    }
}
