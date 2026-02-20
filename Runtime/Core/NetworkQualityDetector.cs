using System;
using System.Threading;

namespace TurboHTTP.Core
{
    public sealed class NetworkQualityDetector
    {
        private readonly object _gate = new object();
        private readonly NetworkQualitySample[] _samples;
        private readonly NetworkQualityThresholds _thresholds;
        private readonly int _promoteAfterConsecutiveWindows;
        private readonly double _ewmaAlpha;

        private int _count;
        private int _nextIndex;
        private int _currentQualityCode;
        private int _consecutiveBetter;
        private int _consecutiveWorse;
        private long _ewmaLatencyMsBits;
        private long _timeoutRatioBits;
        private long _successRatioBits;

        public NetworkQualityDetector(
            int windowSize = 64,
            int promoteAfterConsecutiveWindows = 3,
            double ewmaAlpha = 0.5,
            NetworkQualityThresholds thresholds = null)
        {
            if (windowSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Must be > 0.");
            if (promoteAfterConsecutiveWindows <= 0)
                throw new ArgumentOutOfRangeException(nameof(promoteAfterConsecutiveWindows), "Must be > 0.");
            if (ewmaAlpha <= 0 || ewmaAlpha > 1)
                throw new ArgumentOutOfRangeException(nameof(ewmaAlpha), "Must be in (0, 1].");

            _samples = new NetworkQualitySample[windowSize];
            _thresholds = thresholds ?? new NetworkQualityThresholds();
            _promoteAfterConsecutiveWindows = promoteAfterConsecutiveWindows;
            _ewmaAlpha = ewmaAlpha;
            _currentQualityCode = (int)NetworkQuality.Good;
            WriteAtomicDouble(ref _successRatioBits, 1d);
        }

        public void AddSample(NetworkQualitySample sample)
        {
            lock (_gate)
            {
                _samples[_nextIndex] = sample;
                _nextIndex = (_nextIndex + 1) % _samples.Length;
                if (_count < _samples.Length)
                    _count++;

                RecomputeMetrics_NoLock();
                UpdateQuality_NoLock();
            }
        }

        public NetworkQualitySnapshot GetSnapshot()
        {
            return new NetworkQualitySnapshot(
                (NetworkQuality)Volatile.Read(ref _currentQualityCode),
                ReadAtomicDouble(ref _ewmaLatencyMsBits),
                ReadAtomicDouble(ref _timeoutRatioBits),
                ReadAtomicDouble(ref _successRatioBits),
                Volatile.Read(ref _count));
        }

        private void RecomputeMetrics_NoLock()
        {
            if (_count == 0)
            {
                WriteAtomicDouble(ref _ewmaLatencyMsBits, 0d);
                WriteAtomicDouble(ref _timeoutRatioBits, 0d);
                WriteAtomicDouble(ref _successRatioBits, 1d);
                return;
            }

            double latencyEwma = 0;
            double timeoutEwma = 0;
            double successEwma = 0;
            var oldestIndex = _count == _samples.Length ? _nextIndex : 0;

            for (int offset = 0; offset < _count; offset++)
            {
                var index = (oldestIndex + offset) % _samples.Length;
                var sample = _samples[index];
                var timeoutValue = sample.WasTimeout ? 1d : 0d;
                var successValue = sample.WasSuccess ? 1d : 0d;

                if (offset == 0)
                {
                    latencyEwma = sample.LatencyMs;
                    timeoutEwma = timeoutValue;
                    successEwma = successValue;
                }
                else
                {
                    latencyEwma = (_ewmaAlpha * sample.LatencyMs) + ((1d - _ewmaAlpha) * latencyEwma);
                    timeoutEwma = (_ewmaAlpha * timeoutValue) + ((1d - _ewmaAlpha) * timeoutEwma);
                    successEwma = (_ewmaAlpha * successValue) + ((1d - _ewmaAlpha) * successEwma);
                }
            }

            WriteAtomicDouble(ref _ewmaLatencyMsBits, latencyEwma);
            WriteAtomicDouble(ref _timeoutRatioBits, timeoutEwma);
            WriteAtomicDouble(ref _successRatioBits, successEwma);
        }

        private void UpdateQuality_NoLock()
        {
            var computed = Classify_NoLock();
            var current = (NetworkQuality)_currentQualityCode;

            if (computed == current)
            {
                _consecutiveBetter = 0;
                _consecutiveWorse = 0;
                return;
            }

            if (IsWorse(computed, current))
            {
                _consecutiveBetter = 0;
                if (ShouldDemoteImmediately_NoLock(computed))
                {
                    _currentQualityCode = (int)computed;
                    _consecutiveWorse = 0;
                    return;
                }

                _consecutiveWorse++;
                if (_consecutiveWorse >= _promoteAfterConsecutiveWindows)
                {
                    _currentQualityCode = (int)DemoteOneLevel(current, computed);
                    _consecutiveWorse = 0;
                }
                return;
            }

            _consecutiveWorse = 0;
            _consecutiveBetter++;
            if (_consecutiveBetter >= _promoteAfterConsecutiveWindows)
            {
                _currentQualityCode = (int)computed;
                _consecutiveBetter = 0;
            }
        }

        private NetworkQuality Classify_NoLock()
        {
            if (_count == 0)
                return NetworkQuality.Good;

            var ewmaLatencyMs = ReadAtomicDouble(ref _ewmaLatencyMsBits);
            var timeoutRatio = ReadAtomicDouble(ref _timeoutRatioBits);
            var successRatio = ReadAtomicDouble(ref _successRatioBits);

            if (ewmaLatencyMs < _thresholds.ExcellentLatencyMs &&
                timeoutRatio < _thresholds.ExcellentTimeoutRatio &&
                successRatio >= _thresholds.ExcellentSuccessRatio)
            {
                return NetworkQuality.Excellent;
            }

            if (ewmaLatencyMs < _thresholds.GoodLatencyMs &&
                timeoutRatio < _thresholds.GoodTimeoutRatio &&
                successRatio >= _thresholds.GoodSuccessRatio)
            {
                return NetworkQuality.Good;
            }

            if (ewmaLatencyMs < _thresholds.FairLatencyMs &&
                timeoutRatio < _thresholds.FairTimeoutRatio &&
                successRatio >= _thresholds.FairSuccessRatio)
            {
                return NetworkQuality.Fair;
            }

            return NetworkQuality.Poor;
        }

        private static bool IsWorse(NetworkQuality candidate, NetworkQuality current)
        {
            return (int)candidate > (int)current;
        }

        private bool ShouldDemoteImmediately_NoLock(NetworkQuality computed)
        {
            return computed == NetworkQuality.Poor &&
                   ReadAtomicDouble(ref _timeoutRatioBits) >= _thresholds.FairTimeoutRatio;
        }

        private static NetworkQuality DemoteOneLevel(NetworkQuality current, NetworkQuality computed)
        {
            if (!IsWorse(computed, current))
                return current;

            var oneStep = (NetworkQuality)Math.Min((int)current + 1, (int)NetworkQuality.Poor);
            return IsWorse(computed, oneStep) ? oneStep : computed;
        }

        private static void WriteAtomicDouble(ref long targetBits, double value)
        {
            Volatile.Write(ref targetBits, BitConverter.DoubleToInt64Bits(value));
        }

        private static double ReadAtomicDouble(ref long sourceBits)
        {
            return BitConverter.Int64BitsToDouble(Volatile.Read(ref sourceBits));
        }
    }
}
