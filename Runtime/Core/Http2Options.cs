using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Configuration options for the HTTP/2 connection manager and client settings.
    /// </summary>
    public sealed class Http2Options
    {
        private bool _enablePush = true;
        private int _maxConcurrentStreams = 100;
        private long _maxResponseBodySize = 100 * 1024 * 1024; // 100 MB default
        private int _initialWindowSize = 65535; // Http2Constants.DefaultInitialWindowSize
        private int _maxFrameSize = 16384; // Http2Constants.DefaultMaxFrameSize
        private int _maxHeaderListSize = 64 * 1024; // 64 KB default
        private int _maxDecodedHeaderBytes = UHttpClientOptions.DefaultHttp2MaxDecodedHeaderBytes;

        /// <summary>
        /// Advertised ENABLE_PUSH setting in the initial SETTINGS frame.
        /// RFC 7540 Section 6.5.2. Defaults to true.
        /// </summary>
        public bool EnablePush
        {
            get => _enablePush;
            set => _enablePush = value;
        }

        /// <summary>
        /// Advertised MAX_CONCURRENT_STREAMS setting in the initial SETTINGS frame.
        /// RFC 7540 Section 6.5.2. Defaults to 100.
        /// </summary>
        public int MaxConcurrentStreams
        {
            get => _maxConcurrentStreams;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Must be non-negative.");
                _maxConcurrentStreams = value;
            }
        }

        /// <summary>
        /// Maximum allowed response body size in bytes. Protects against unbounded
        /// memory consumption from malicious or misconfigured servers. Defaults to 100 MB.
        /// Set to 0 for unlimited.
        /// </summary>
        public long MaxResponseBodySize
        {
            get => _maxResponseBodySize;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Must be non-negative.");
                _maxResponseBodySize = value;
            }
        }

        /// <summary>
        /// Advertised INITIAL_WINDOW_SIZE in the initial SETTINGS frame.
        /// RFC 7540 Section 6.5.2. Defaults to 65535 (64 KB - 1).
        /// </summary>
        public int InitialWindowSize
        {
            get => _initialWindowSize;
            set
            {
                if (value < 0 || value > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be between 0 and 2147483647.");
                _initialWindowSize = value;
            }
        }

        /// <summary>
        /// Advertised MAX_FRAME_SIZE in the initial SETTINGS frame.
        /// RFC 7540 Section 6.5.2. Defaults to 16384.
        /// </summary>
        public int MaxFrameSize
        {
            get => _maxFrameSize;
            set
            {
                if (value < 16384 || value > 16777215)
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be between 16384 and 16777215.");
                _maxFrameSize = value;
            }
        }

        /// <summary>
        /// Advertised MAX_HEADER_LIST_SIZE in the initial SETTINGS frame.
        /// Defines the maximum uncompressed header size the client is willing to accept.
        /// Defaults to 65536 (64 KB).
        /// </summary>
        public int MaxHeaderListSize
        {
            get => _maxHeaderListSize;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Must be greater than 0.");
                _maxHeaderListSize = value;
            }
        }

        /// <summary>
        /// Maximum total decoded HTTP/2 header bytes (name + value) allowed per
        /// header block. Used as decompression-bomb protection for HPACK decoding.
        /// Default is 256KB.
        /// </summary>
        public int MaxDecodedHeaderBytes
        {
            get => _maxDecodedHeaderBytes;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Must be greater than 0.");
                _maxDecodedHeaderBytes = value;
            }
        }

        public Http2Options Clone()
        {
            return new Http2Options
            {
                _enablePush = _enablePush,
                _maxConcurrentStreams = _maxConcurrentStreams,
                _maxResponseBodySize = _maxResponseBodySize,
                _initialWindowSize = _initialWindowSize,
                _maxFrameSize = _maxFrameSize,
                _maxHeaderListSize = _maxHeaderListSize,
                _maxDecodedHeaderBytes = _maxDecodedHeaderBytes
            };
        }

        internal bool IsDefault()
        {
            return _enablePush == true &&
                   _maxConcurrentStreams == 100 &&
                   _maxResponseBodySize == 100 * 1024 * 1024 &&
                   _initialWindowSize == 65535 &&
                   _maxFrameSize == 16384 &&
                   _maxHeaderListSize == 64 * 1024 &&
                   _maxDecodedHeaderBytes == UHttpClientOptions.DefaultHttp2MaxDecodedHeaderBytes;
        }
    }
}
