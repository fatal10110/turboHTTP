using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Snapshot of observable WebSocket connection counters.
    /// </summary>
    public readonly struct WebSocketMetrics
    {
        /// <summary>Creates a metrics snapshot.</summary>
        public WebSocketMetrics(
            long bytesSent,
            long bytesReceived,
            long messagesSent,
            long messagesReceived,
            long framesSent,
            long framesReceived,
            long pingsSent,
            long pongsReceived,
            long uncompressedBytesSent,
            long compressedBytesSent,
            long compressedBytesReceived,
            TimeSpan connectionUptime,
            TimeSpan lastActivityAge)
        {
            BytesSent = bytesSent;
            BytesReceived = bytesReceived;
            MessagesSent = messagesSent;
            MessagesReceived = messagesReceived;
            FramesSent = framesSent;
            FramesReceived = framesReceived;
            PingsSent = pingsSent;
            PongsReceived = pongsReceived;
            UncompressedBytesSent = uncompressedBytesSent;
            CompressedBytesSent = compressedBytesSent;
            CompressedBytesReceived = compressedBytesReceived;
            ConnectionUptime = connectionUptime;
            LastActivityAge = lastActivityAge;
        }

        /// <summary>Bytes written to the connection, frame headers included.</summary>
        public long BytesSent { get; }

        /// <summary>Bytes read from the connection, frame headers included.</summary>
        public long BytesReceived { get; }

        /// <summary>Data messages sent.</summary>
        public long MessagesSent { get; }

        /// <summary>Data messages received.</summary>
        public long MessagesReceived { get; }

        /// <summary>Frames sent, control frames included.</summary>
        public long FramesSent { get; }

        /// <summary>Frames received, control frames included.</summary>
        public long FramesReceived { get; }

        /// <summary>Keep-alive pings sent.</summary>
        public long PingsSent { get; }

        /// <summary>Pongs received.</summary>
        public long PongsReceived { get; }

        /// <summary>Outbound payload bytes before permessage-deflate compression.</summary>
        public long UncompressedBytesSent { get; }

        /// <summary>Outbound payload bytes after permessage-deflate compression.</summary>
        public long CompressedBytesSent { get; }

        /// <summary>Inbound payload bytes before permessage-deflate decompression.</summary>
        public long CompressedBytesReceived { get; }

        /// <summary><see cref="UncompressedBytesSent"/> divided by <see cref="CompressedBytesSent"/>, or 1 when nothing was compressed.</summary>
        public double CompressionRatio =>
            CompressedBytesSent > 0
                ? (double)UncompressedBytesSent / CompressedBytesSent
                : 1.0d;

        /// <summary>Time since the connection opened.</summary>
        public TimeSpan ConnectionUptime { get; }

        /// <summary>Time since the last frame was sent or received.</summary>
        public TimeSpan LastActivityAge { get; }
    }
}
