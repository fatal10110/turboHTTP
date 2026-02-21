using System;
using System.Threading;

namespace TurboHTTP.WebSocket
{
    internal sealed class WebSocketMetricsCollector
    {
        private static readonly double StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;

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
            _connectedAtStopwatchTimestamp = now;
            _lastActivityStopwatchTimestamp = now;
            _lastPublishStopwatchTimestamp = now;
        }

        public void RecordFrameSent(int byteCount)
        {
            if (byteCount > 0)
                Interlocked.Add(ref _bytesSent, byteCount);

            Interlocked.Increment(ref _framesSent);
            TouchActivity();
        }

        public void RecordFramesSent(int frameCount, int byteCount)
        {
            if (byteCount > 0)
                Interlocked.Add(ref _bytesSent, byteCount);

            if (frameCount > 0)
                Interlocked.Add(ref _framesSent, frameCount);

            TouchActivity();
        }

        public void RecordFrameReceived(int byteCount)
        {
            if (byteCount > 0)
                Interlocked.Add(ref _bytesReceived, byteCount);

            Interlocked.Increment(ref _framesReceived);
            TouchActivity();
        }

        public void RecordFramesReceived(int frameCount, int byteCount)
        {
            if (byteCount > 0)
                Interlocked.Add(ref _bytesReceived, byteCount);

            if (frameCount > 0)
                Interlocked.Add(ref _framesReceived, frameCount);

            TouchActivity();
        }

        public void RecordMessageSent()
        {
            Interlocked.Increment(ref _messagesSent);
            Interlocked.Increment(ref _messageEventsSinceLastPublish);
            TouchActivity();
        }

        public void RecordMessageReceived()
        {
            Interlocked.Increment(ref _messagesReceived);
            Interlocked.Increment(ref _messageEventsSinceLastPublish);
            TouchActivity();
        }

        public void RecordCompression(int originalSize, int compressedSize)
        {
            if (originalSize > 0)
                Interlocked.Add(ref _uncompressedBytesSent, originalSize);

            if (compressedSize > 0)
                Interlocked.Add(ref _compressedBytesSent, compressedSize);
        }

        public void RecordCompressedInboundBytes(int compressedSize)
        {
            if (compressedSize > 0)
                Interlocked.Add(ref _compressedBytesReceived, compressedSize);
        }

        public void RecordPingSent()
        {
            Interlocked.Increment(ref _pingsSent);
            TouchActivity();
        }

        public void RecordPongReceived()
        {
            Interlocked.Increment(ref _pongsReceived);
            TouchActivity();
        }

        public bool ShouldPublishSnapshot(int messageInterval, TimeSpan minInterval)
        {
            bool reachedMessageInterval = messageInterval > 0 &&
                Volatile.Read(ref _messageEventsSinceLastPublish) >= messageInterval;
            bool reachedTimeInterval = minInterval > TimeSpan.Zero &&
                ElapsedSince(Volatile.Read(ref _lastPublishStopwatchTimestamp)) >= minInterval;

            if (!reachedMessageInterval && !reachedTimeInterval)
                return false;

            Interlocked.Exchange(ref _messageEventsSinceLastPublish, 0);
            Interlocked.Exchange(
                ref _lastPublishStopwatchTimestamp,
                System.Diagnostics.Stopwatch.GetTimestamp());
            return true;
        }

        public WebSocketMetrics GetSnapshot()
        {
            long bytesSent = Volatile.Read(ref _bytesSent);
            long bytesReceived = Volatile.Read(ref _bytesReceived);
            long messagesSent = Volatile.Read(ref _messagesSent);
            long messagesReceived = Volatile.Read(ref _messagesReceived);
            long framesSent = Volatile.Read(ref _framesSent);
            long framesReceived = Volatile.Read(ref _framesReceived);
            long pingsSent = Volatile.Read(ref _pingsSent);
            long pongsReceived = Volatile.Read(ref _pongsReceived);
            long uncompressedBytesSent = Volatile.Read(ref _uncompressedBytesSent);
            long compressedBytesSent = Volatile.Read(ref _compressedBytesSent);
            long compressedBytesReceived = Volatile.Read(ref _compressedBytesReceived);

            TimeSpan uptime = ElapsedSince(Volatile.Read(ref _connectedAtStopwatchTimestamp));
            TimeSpan lastActivityAge = ElapsedSince(Volatile.Read(ref _lastActivityStopwatchTimestamp));

            return new WebSocketMetrics(
                bytesSent,
                bytesReceived,
                messagesSent,
                messagesReceived,
                framesSent,
                framesReceived,
                pingsSent,
                pongsReceived,
                uncompressedBytesSent,
                compressedBytesSent,
                compressedBytesReceived,
                uptime,
                lastActivityAge);
        }

        private void TouchActivity()
        {
            Interlocked.Exchange(
                ref _lastActivityStopwatchTimestamp,
                System.Diagnostics.Stopwatch.GetTimestamp());
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
