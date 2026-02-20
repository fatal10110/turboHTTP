namespace TurboHTTP.Core
{
    public enum NetworkQuality
    {
        Excellent = 0,
        Good = 1,
        Fair = 2,
        Poor = 3
    }

    public readonly struct NetworkQualitySample
    {
        public NetworkQualitySample(
            double latencyMs,
            double totalDurationMs,
            bool wasTimeout,
            bool wasTransportFailure,
            long bytesTransferred,
            bool wasSuccess)
        {
            LatencyMs = latencyMs;
            TotalDurationMs = totalDurationMs;
            WasTimeout = wasTimeout;
            WasTransportFailure = wasTransportFailure;
            BytesTransferred = bytesTransferred;
            WasSuccess = wasSuccess;
        }

        public double LatencyMs { get; }
        public double TotalDurationMs { get; }
        public bool WasTimeout { get; }
        public bool WasTransportFailure { get; }
        public long BytesTransferred { get; }
        public bool WasSuccess { get; }
    }

    public readonly struct NetworkQualitySnapshot
    {
        public NetworkQualitySnapshot(
            NetworkQuality quality,
            double ewmaLatencyMs,
            double timeoutRatio,
            double successRatio,
            int sampleCount)
        {
            Quality = quality;
            EwmaLatencyMs = ewmaLatencyMs;
            TimeoutRatio = timeoutRatio;
            SuccessRatio = successRatio;
            SampleCount = sampleCount;
        }

        public NetworkQuality Quality { get; }
        public double EwmaLatencyMs { get; }
        public double TimeoutRatio { get; }
        public double SuccessRatio { get; }
        public int SampleCount { get; }
    }

    public sealed class NetworkQualityThresholds
    {
        public double ExcellentLatencyMs { get; set; } = 120;
        public double GoodLatencyMs { get; set; } = 300;
        public double FairLatencyMs { get; set; } = 900;

        public double ExcellentTimeoutRatio { get; set; } = 0.01;
        public double GoodTimeoutRatio { get; set; } = 0.03;
        public double FairTimeoutRatio { get; set; } = 0.08;

        public double ExcellentSuccessRatio { get; set; } = 0.99;
        public double GoodSuccessRatio { get; set; } = 0.97;
        public double FairSuccessRatio { get; set; } = 0.90;
    }
}
