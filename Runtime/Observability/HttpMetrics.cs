using System;
using System.Collections.Concurrent;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// HTTP metrics collected by MetricsMiddleware.
    /// Long fields are updated atomically via Interlocked operations.
    /// AverageResponseTimeMs is stored as long bits (via BitConverter) for
    /// atomicity on 32-bit IL2CPP where double writes can tear.
    /// Read access is eventually consistent during concurrent writes.
    /// </summary>
    public class HttpMetrics
    {
        // Use public fields (not properties) for Interlocked compatibility.
        // Interlocked requires ref to field, which doesn't work with property backing fields.
        public long TotalRequests;
        public long SuccessfulRequests;
        public long FailedRequests;
        public long TotalBytesReceived;
        public long TotalBytesSent;

        // Stored as long bits for atomic read/write on 32-bit platforms.
        // Use GetAverageResponseTimeMs() / SetAverageResponseTimeMs() accessors.
        internal long AverageResponseTimeMsBits;

        public double GetAverageResponseTimeMs()
        {
            long bits = System.Threading.Interlocked.Read(ref AverageResponseTimeMsBits);
            return BitConverter.Int64BitsToDouble(bits);
        }

        internal void SetAverageResponseTimeMs(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            System.Threading.Interlocked.Exchange(ref AverageResponseTimeMsBits, bits);
        }

        /// <summary>
        /// Request count per host (e.g., "api.example.com" -> 42).
        /// </summary>
        public ConcurrentDictionary<string, long> RequestsByHost { get; } =
            new ConcurrentDictionary<string, long>();

        /// <summary>
        /// Request count per HTTP status code (e.g., 200 -> 100, 404 -> 5).
        /// </summary>
        public ConcurrentDictionary<int, long> RequestsByStatusCode { get; } =
            new ConcurrentDictionary<int, long>();
    }
}
