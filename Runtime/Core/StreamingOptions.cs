using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Runtime-configurable thresholds for buffered and streaming request/response paths.
    /// </summary>
    public sealed class StreamingOptions
    {
        private const int DefaultSmallBufferedRequestThresholdBytesValue = 32 * 1024;
        private const int DefaultStreamingSendBufferBytesValue = 32 * 1024;
        private const int DefaultStreamingReceiveBufferBytesValue = 64 * 1024;
        private const int DefaultHttp2PerStreamReceiveBufferBytesValue = 256 * 1024;
        private const int DefaultBufferedDrainReuseThresholdBytesValue = 64 * 1024;
        private const int DefaultMaxConnectionBufferedBytesValue = 8 * 1024 * 1024;
        private const int DefaultHttp2StallTimeoutSecondsValue = 60;
        private const int DefaultExpectContinueTimeoutMsValue = 1000;

        private int _smallBufferedRequestThresholdBytes = DefaultSmallBufferedRequestThresholdBytesValue;
        private int _defaultStreamingSendBufferBytes = DefaultStreamingSendBufferBytesValue;
        private int _defaultStreamingReceiveBufferBytes = DefaultStreamingReceiveBufferBytesValue;
        private int _defaultHttp2PerStreamReceiveBufferBytes = DefaultHttp2PerStreamReceiveBufferBytesValue;
        private int _bufferedDrainReuseThresholdBytes = DefaultBufferedDrainReuseThresholdBytesValue;
        private int _maxConnectionBufferedBytes = DefaultMaxConnectionBufferedBytesValue;
        private int _http2StallTimeoutSeconds = DefaultHttp2StallTimeoutSecondsValue;
        private int _expectContinueTimeoutMs = DefaultExpectContinueTimeoutMsValue;
        private long? _autoExpectContinueThresholdBytes;

        /// <summary>
        /// Gets or sets the size in bytes up to which a buffered request body is sent in a single write. Must be positive. Default 32 KiB.
        /// </summary>
        public int SmallBufferedRequestThresholdBytes
        {
            get => _smallBufferedRequestThresholdBytes;
            set => _smallBufferedRequestThresholdBytes = ValidatePositive(value, nameof(SmallBufferedRequestThresholdBytes));
        }

        /// <summary>
        /// Gets or sets the buffer size in bytes used when sending a streamed request body. Must be positive. Default 32 KiB.
        /// </summary>
        public int DefaultStreamingSendBufferBytes
        {
            get => _defaultStreamingSendBufferBytes;
            set => _defaultStreamingSendBufferBytes = ValidatePositive(value, nameof(DefaultStreamingSendBufferBytes));
        }

        /// <summary>
        /// Gets or sets the buffer size in bytes used when receiving a streamed response body. Must be positive. Default 64 KiB.
        /// </summary>
        public int DefaultStreamingReceiveBufferBytes
        {
            get => _defaultStreamingReceiveBufferBytes;
            set => _defaultStreamingReceiveBufferBytes = ValidatePositive(value, nameof(DefaultStreamingReceiveBufferBytes));
        }

        /// <summary>
        /// Preferred buffered receive capacity per HTTP/2 stream.
        /// The effective transport buffer may be raised to satisfy the negotiated
        /// flow-control window or max frame size required by the connection.
        /// </summary>
        public int DefaultHttp2PerStreamReceiveBufferBytes
        {
            get => _defaultHttp2PerStreamReceiveBufferBytes;
            set => _defaultHttp2PerStreamReceiveBufferBytes = ValidatePositive(value, nameof(DefaultHttp2PerStreamReceiveBufferBytes));
        }

        /// <summary>
        /// Gets or sets the largest unread response remainder in bytes the transport drains to keep the connection reusable;
        /// more than that and the connection is closed. Must be positive. Default 64 KiB.
        /// </summary>
        public int BufferedDrainReuseThresholdBytes
        {
            get => _bufferedDrainReuseThresholdBytes;
            set => _bufferedDrainReuseThresholdBytes = ValidatePositive(value, nameof(BufferedDrainReuseThresholdBytes));
        }

        /// <summary>
        /// Gets or sets the upper bound in bytes of response data buffered per HTTP/2 connection (HTTP/1.1 does not read it). Must be positive. Default 8 MiB.
        /// </summary>
        public int MaxConnectionBufferedBytes
        {
            get => _maxConnectionBufferedBytes;
            set => _maxConnectionBufferedBytes = ValidatePositive(value, nameof(MaxConnectionBufferedBytes));
        }

        /// <summary>
        /// Seconds an HTTP/2 connection waits without progress before it gives up: a buffered response
        /// body the consumer stopped reading is aborted, and a frame write the peer stopped accepting
        /// fails the whole connection with a network error. Default 60.
        /// </summary>
        public int Http2StallTimeoutSeconds
        {
            get => _http2StallTimeoutSeconds;
            set => _http2StallTimeoutSeconds = ValidatePositive(value, nameof(Http2StallTimeoutSeconds));
        }

        /// <summary>
        /// Timeout in milliseconds to wait for a 100 Continue response before proceeding
        /// with request body transmission (RFC 9110 section 10.1.1: a client does not wait indefinitely).
        /// Applies to requests carrying <c>Expect: 100-continue</c>, see <see cref="UHttpRequest.WithExpectContinue"/>.
        /// </summary>
        public int ExpectContinueTimeoutMs
        {
            get => _expectContinueTimeoutMs;
            set => _expectContinueTimeoutMs = ValidatePositive(value, nameof(ExpectContinueTimeoutMs));
        }

        /// <summary>
        /// Optional known-length threshold for automatically injecting Expect: 100-continue.
        /// A null value disables the automatic path. A value of 0 injects the header for all
        /// known-length non-empty bodies.
        /// </summary>
        public long? AutoExpectContinueThresholdBytes
        {
            get => _autoExpectContinueThresholdBytes;
            set => _autoExpectContinueThresholdBytes = ValidateNonNegativeNullable(
                value,
                nameof(AutoExpectContinueThresholdBytes));
        }

        /// <summary>
        /// Creates a copy of these options.
        /// </summary>
        public StreamingOptions Clone()
        {
            return new StreamingOptions
            {
                _smallBufferedRequestThresholdBytes = _smallBufferedRequestThresholdBytes,
                _defaultStreamingSendBufferBytes = _defaultStreamingSendBufferBytes,
                _defaultStreamingReceiveBufferBytes = _defaultStreamingReceiveBufferBytes,
                _defaultHttp2PerStreamReceiveBufferBytes = _defaultHttp2PerStreamReceiveBufferBytes,
                _bufferedDrainReuseThresholdBytes = _bufferedDrainReuseThresholdBytes,
                _maxConnectionBufferedBytes = _maxConnectionBufferedBytes,
                _http2StallTimeoutSeconds = _http2StallTimeoutSeconds,
                _expectContinueTimeoutMs = _expectContinueTimeoutMs,
                _autoExpectContinueThresholdBytes = _autoExpectContinueThresholdBytes
            };
        }

        internal bool IsDefault()
        {
            return _smallBufferedRequestThresholdBytes == DefaultSmallBufferedRequestThresholdBytesValue &&
                   _defaultStreamingSendBufferBytes == DefaultStreamingSendBufferBytesValue &&
                   _defaultStreamingReceiveBufferBytes == DefaultStreamingReceiveBufferBytesValue &&
                   _defaultHttp2PerStreamReceiveBufferBytes == DefaultHttp2PerStreamReceiveBufferBytesValue &&
                   _bufferedDrainReuseThresholdBytes == DefaultBufferedDrainReuseThresholdBytesValue &&
                   _maxConnectionBufferedBytes == DefaultMaxConnectionBufferedBytesValue &&
                   _http2StallTimeoutSeconds == DefaultHttp2StallTimeoutSecondsValue &&
                   _expectContinueTimeoutMs == DefaultExpectContinueTimeoutMsValue &&
                   !_autoExpectContinueThresholdBytes.HasValue;
        }

        private static int ValidatePositive(int value, string paramName)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(paramName, "Must be greater than 0.");

            return value;
        }

        private static long? ValidateNonNegativeNullable(long? value, string paramName)
        {
            if (value.HasValue && value.Value < 0)
                throw new ArgumentOutOfRangeException(paramName, "Must be greater than or equal to 0.");

            return value;
        }
    }
}
