using System;

namespace TurboHTTP.WebSocket
{
    public enum ConnectionQuality
    {
        Unknown = 0,
        Excellent = 1,
        Good = 2,
        Fair = 3,
        Poor = 4,
        Critical = 5
    }

    public readonly struct WebSocketHealthSnapshot
    {
        public static readonly WebSocketHealthSnapshot Unknown = new WebSocketHealthSnapshot(
            currentRtt: TimeSpan.Zero,
            averageRtt: TimeSpan.Zero,
            rttJitter: TimeSpan.Zero,
            recentThroughput: 0d,
            quality: ConnectionQuality.Unknown);

        public WebSocketHealthSnapshot(
            TimeSpan currentRtt,
            TimeSpan averageRtt,
            TimeSpan rttJitter,
            double recentThroughput,
            ConnectionQuality quality)
        {
            CurrentRtt = currentRtt;
            AverageRtt = averageRtt;
            RttJitter = rttJitter;
            RecentThroughput = recentThroughput;
            Quality = quality;
        }

        public TimeSpan CurrentRtt { get; }

        public TimeSpan AverageRtt { get; }

        public TimeSpan RttJitter { get; }

        public double RecentThroughput { get; }

        public ConnectionQuality Quality { get; }
    }

    internal sealed class WebSocketHealthMonitor
    {
        private static readonly double StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;

        private readonly object _gate = new object();
        private readonly TimeSpan[] _rttWindow = new TimeSpan[10];

        private int _rttWindowCount;
        private int _rttWindowWriteIndex;
        private int _baselineSampleCount;

        private TimeSpan _baselineRtt;
        private TimeSpan _currentRtt;
        private TimeSpan _averageRtt;
        private TimeSpan _rttJitter;
        private ConnectionQuality _quality = ConnectionQuality.Unknown;

        private long _lastThroughputBytesTotal;
        private long _lastThroughputStopwatchTimestamp;
        private double _recentThroughput;

        public event Action<ConnectionQuality> OnQualityChanged;

        public void RecordMetricsSnapshot(WebSocketMetrics metrics)
        {
            ConnectionQuality? changedQuality = null;

            lock (_gate)
            {
                UpdateThroughput(metrics);
                changedQuality = UpdateQuality_NoLock(metrics);
            }

            if (changedQuality.HasValue)
                OnQualityChanged?.Invoke(changedQuality.Value);
        }

        public void RecordRttSample(TimeSpan rtt, WebSocketMetrics metrics)
        {
            if (rtt < TimeSpan.Zero)
                rtt = TimeSpan.Zero;

            ConnectionQuality? changedQuality = null;

            lock (_gate)
            {
                _currentRtt = rtt;
                _rttWindow[_rttWindowWriteIndex] = rtt;
                _rttWindowWriteIndex = (_rttWindowWriteIndex + 1) % _rttWindow.Length;
                if (_rttWindowCount < _rttWindow.Length)
                    _rttWindowCount++;

                RecomputeRttStatistics_NoLock();
                if (_baselineSampleCount < 3)
                {
                    _baselineSampleCount++;
                    if (_baselineSampleCount == 1)
                    {
                        _baselineRtt = rtt;
                    }
                    else
                    {
                        double combinedMs =
                            (_baselineRtt.TotalMilliseconds * (_baselineSampleCount - 1)) + rtt.TotalMilliseconds;
                        _baselineRtt = TimeSpan.FromMilliseconds(combinedMs / _baselineSampleCount);
                    }
                }

                changedQuality = UpdateQuality_NoLock(metrics);
            }

            if (changedQuality.HasValue)
                OnQualityChanged?.Invoke(changedQuality.Value);
        }

        public WebSocketHealthSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new WebSocketHealthSnapshot(
                    _currentRtt,
                    _averageRtt,
                    _rttJitter,
                    _recentThroughput,
                    _quality);
            }
        }

        private void UpdateThroughput(WebSocketMetrics metrics)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long totalBytes = unchecked(metrics.BytesSent + metrics.BytesReceived);

            if (_lastThroughputStopwatchTimestamp > 0)
            {
                long deltaTicks = now - _lastThroughputStopwatchTimestamp;
                if (deltaTicks > 0)
                {
                    long deltaBytes = totalBytes - _lastThroughputBytesTotal;
                    if (deltaBytes < 0)
                        deltaBytes = 0;

                    double seconds = deltaTicks / StopwatchFrequency;
                    if (seconds > 0)
                        _recentThroughput = deltaBytes / seconds;
                }
            }

            _lastThroughputBytesTotal = totalBytes;
            _lastThroughputStopwatchTimestamp = now;
        }

        private void RecomputeRttStatistics_NoLock()
        {
            if (_rttWindowCount == 0)
            {
                _averageRtt = TimeSpan.Zero;
                _rttJitter = TimeSpan.Zero;
                return;
            }

            double sum = 0d;
            for (int i = 0; i < _rttWindowCount; i++)
                sum += _rttWindow[i].TotalMilliseconds;

            double meanMs = sum / _rttWindowCount;
            _averageRtt = TimeSpan.FromMilliseconds(meanMs);

            double varianceSum = 0d;
            for (int i = 0; i < _rttWindowCount; i++)
            {
                double delta = _rttWindow[i].TotalMilliseconds - meanMs;
                varianceSum += delta * delta;
            }

            _rttJitter = TimeSpan.FromMilliseconds(Math.Sqrt(varianceSum / _rttWindowCount));
        }

        private ConnectionQuality? UpdateQuality_NoLock(WebSocketMetrics metrics)
        {
            var nextQuality = ComputeQuality_NoLock(metrics);
            if (nextQuality == _quality)
                return null;

            _quality = nextQuality;
            return nextQuality;
        }

        private ConnectionQuality ComputeQuality_NoLock(WebSocketMetrics metrics)
        {
            if (_baselineSampleCount < 3)
                return ConnectionQuality.Unknown;

            double baselineMs = Math.Max(1d, _baselineRtt.TotalMilliseconds);
            double avgMs = Math.Max(1d, _averageRtt.TotalMilliseconds);
            double rttRatio = avgMs / baselineMs;
            double rttScore = Clamp01(1d / rttRatio);

            long pings = metrics.PingsSent;
            long pongs = metrics.PongsReceived;
            double lossRate = pings <= 0
                ? 0d
                : Clamp01((double)Math.Max(0L, pings - pongs) / pings);

            double qualityScore = (0.6d * rttScore) + (0.4d * (1d - lossRate));

            if (qualityScore >= 0.9d) return ConnectionQuality.Excellent;
            if (qualityScore >= 0.7d) return ConnectionQuality.Good;
            if (qualityScore >= 0.5d) return ConnectionQuality.Fair;
            if (qualityScore >= 0.3d) return ConnectionQuality.Poor;
            return ConnectionQuality.Critical;
        }

        private static double Clamp01(double value)
        {
            if (value < 0d)
                return 0d;
            if (value > 1d)
                return 1d;
            return value;
        }
    }
}
