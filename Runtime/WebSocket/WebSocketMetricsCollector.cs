using System;

namespace TurboHTTP.WebSocket
{
    internal sealed class WebSocketMetricsCollector
    {
        private static readonly double StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;
        // Lock-based counters avoid torn 64-bit writes/reads on 32-bit IL2CPP targets.
        private readonly object _gate = new object();

        private long _bytesSent;
        private long _bytesReceived;
        private long _messagesSent;
        private long _messagesReceived;
        private long _framesSent;
        private long _framesReceived;
        private long _pingsSent;
        private long _pongsReceived;
        private long _uncompressedBytesSent;
        private long _compressedBytesSent;
        private long _compressedBytesReceived;

        private long _connectedAtStopwatchTimestamp;
        private long _lastActivityStopwatchTimestamp;
        private long _lastPublishStopwatchTimestamp;
        private long _messageEventsSinceLastPublish;

        public WebSocketMetricsCollector()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _connectedAtStopwatchTimestamp = now > 0 ? now - 1 : 0;
            _lastActivityStopwatchTimestamp = now;
            _lastPublishStopwatchTimestamp = now;
        }

        public void RecordFrameSent(int byteCount)
        {
            lock (_gate)
            {
                if (byteCount > 0)
                    _bytesSent += byteCount;

                _framesSent++;
                TouchActivity_NoLock();
            }
        }

        public void RecordFramesSent(int frameCount, int byteCount)
        {
            lock (_gate)
            {
                if (byteCount > 0)
                    _bytesSent += byteCount;

                if (frameCount > 0)
                    _framesSent += frameCount;

                TouchActivity_NoLock();
            }
        }

        public void RecordFrameReceived(int byteCount)
        {
            lock (_gate)
            {
                if (byteCount > 0)
                    _bytesReceived += byteCount;

                _framesReceived++;
                TouchActivity_NoLock();
            }
        }

        public void RecordFramesReceived(int frameCount, int byteCount)
        {
            lock (_gate)
            {
                if (byteCount > 0)
                    _bytesReceived += byteCount;

                if (frameCount > 0)
                    _framesReceived += frameCount;

                TouchActivity_NoLock();
            }
        }

        public void RecordMessageSent()
        {
            lock (_gate)
            {
                _messagesSent++;
                _messageEventsSinceLastPublish++;
                TouchActivity_NoLock();
            }
        }

        public void RecordMessageReceived()
        {
            lock (_gate)
            {
                _messagesReceived++;
                _messageEventsSinceLastPublish++;
                TouchActivity_NoLock();
            }
        }

        public void RecordCompression(int originalSize, int compressedSize)
        {
            lock (_gate)
            {
                if (originalSize > 0)
                    _uncompressedBytesSent += originalSize;

                if (compressedSize > 0)
                    _compressedBytesSent += compressedSize;
            }
        }

        public void RecordCompressedInboundBytes(int compressedSize)
        {
            lock (_gate)
            {
                if (compressedSize > 0)
                    _compressedBytesReceived += compressedSize;
            }
        }

        public void RecordPingSent()
        {
            lock (_gate)
            {
                _pingsSent++;
                TouchActivity_NoLock();
            }
        }

        public void RecordPongReceived()
        {
            lock (_gate)
            {
                _pongsReceived++;
                TouchActivity_NoLock();
            }
        }

        public bool ShouldPublishSnapshot(int messageInterval, TimeSpan minInterval)
        {
            lock (_gate)
            {
                bool reachedMessageInterval = messageInterval > 0 &&
                    _messageEventsSinceLastPublish >= messageInterval;
                bool reachedTimeInterval = minInterval > TimeSpan.Zero &&
                    ElapsedSince(_lastPublishStopwatchTimestamp) >= minInterval;

                if (!reachedMessageInterval && !reachedTimeInterval)
                    return false;

                _messageEventsSinceLastPublish = 0;
                _lastPublishStopwatchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                return true;
            }
        }

        public WebSocketMetrics GetSnapshot()
        {
            lock (_gate)
            {
                TimeSpan uptime = ElapsedSince(_connectedAtStopwatchTimestamp);
                if (uptime <= TimeSpan.Zero)
                    uptime = TimeSpan.FromTicks(1);

                TimeSpan lastActivityAge = ElapsedSince(_lastActivityStopwatchTimestamp);

                return new WebSocketMetrics(
                    _bytesSent,
                    _bytesReceived,
                    _messagesSent,
                    _messagesReceived,
                    _framesSent,
                    _framesReceived,
                    _pingsSent,
                    _pongsReceived,
                    _uncompressedBytesSent,
                    _compressedBytesSent,
                    _compressedBytesReceived,
                    uptime,
                    lastActivityAge);
            }
        }

        private void TouchActivity_NoLock()
        {
            _lastActivityStopwatchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static TimeSpan ElapsedSince(long fromTimestamp)
        {
            if (fromTimestamp <= 0)
                return TimeSpan.Zero;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long delta = now - fromTimestamp;
            if (delta <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(delta / StopwatchFrequency);
        }
    }
}
