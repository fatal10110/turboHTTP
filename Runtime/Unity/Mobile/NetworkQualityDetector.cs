using System;

namespace TurboHTTP.Unity.Mobile
{
    /// <summary>
    /// Classifies network quality from a sliding window of request samples.
    /// <para>Thread safety: every mutable field is owned by one lock (<c>_gate</c>);
    /// <see cref="AddSample"/> and <see cref="GetSnapshot"/> both take it, so a snapshot always
    /// describes the state after one complete sample (quality, EWMA values and count agree).</para>
    /// </summary>
    public sealed class NetworkQualityDetector
    {
        // Owns every mutable field below. The state is updated once per completed request,
        // not per frame, so a lock is cheaper to reason about than per-field atomics.
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
        private double _ewmaLatencyMs;
        private double _timeoutRatio;
        private double _successRatio;

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
            _successRatio = 1d;
        }

        /// <summary>Adds one completed-request sample and reclassifies the quality.</summary>
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

        /// <summary>The most recently added sample, for diagnostics and tests.</summary>
        internal bool TryGetLatestSample(out NetworkQualitySample sample)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    sample = default;
                    return false;
                }

                sample = _samples[(_nextIndex - 1 + _samples.Length) % _samples.Length];
                return true;
            }
        }

        /// <summary>A consistent view of the current quality and the metrics it was computed from.</summary>
        public NetworkQualitySnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new NetworkQualitySnapshot(
                    (NetworkQuality)_currentQualityCode,
                    _ewmaLatencyMs,
                    _timeoutRatio,
                    _successRatio,
                    _count);
            }
        }

        private void RecomputeMetrics_NoLock()
        {
            if (_count == 0)
            {
                _ewmaLatencyMs = 0d;
                _timeoutRatio = 0d;
                _successRatio = 1d;
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

            _ewmaLatencyMs = latencyEwma;
            _timeoutRatio = timeoutEwma;
            _successRatio = successEwma;
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

            var ewmaLatencyMs = _ewmaLatencyMs;
            var timeoutRatio = _timeoutRatio;
            var successRatio = _successRatio;

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
                   _timeoutRatio >= _thresholds.FairTimeoutRatio;
        }

        private static NetworkQuality DemoteOneLevel(NetworkQuality current, NetworkQuality computed)
        {
            if (!IsWorse(computed, current))
                return current;

            var oneStep = (NetworkQuality)Math.Min((int)current + 1, (int)NetworkQuality.Poor);
            return IsWorse(computed, oneStep) ? oneStep : computed;
        }
    }
}
