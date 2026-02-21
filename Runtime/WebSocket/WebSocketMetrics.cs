using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Snapshot of observable WebSocket connection counters.
    /// </summary>
    public readonly struct WebSocketMetrics
    {
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

        public long BytesSent { get; }

        public long BytesReceived { get; }

        public long MessagesSent { get; }

        public long MessagesReceived { get; }

        public long FramesSent { get; }

        public long FramesReceived { get; }

        public long PingsSent { get; }

        public long PongsReceived { get; }

        public long UncompressedBytesSent { get; }

        public long CompressedBytesSent { get; }

        public long CompressedBytesReceived { get; }

        public double CompressionRatio =>
            CompressedBytesSent > 0
                ? (double)UncompressedBytesSent / CompressedBytesSent
                : 1.0d;

        public TimeSpan ConnectionUptime { get; }

        public TimeSpan LastActivityAge { get; }
    }
}
