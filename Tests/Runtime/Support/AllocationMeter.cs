using System;
using System.Collections;
using TurboHTTP.Tests.Transport;
using Unity.Profiling;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Managed bytes allocated by a synchronous section. Uses <c>GC.GetAllocatedBytesForCurrentThread</c> where
    /// probes show it counts exactly the calling thread's allocations. Unity's Mono (Boehm GC) returns 0 there
    /// (#426) and Unity 2021.3's IL2CPP rounds to 4 KiB and includes other threads (#438), so on both the meter
    /// falls back to the profiler's "GC Allocated In Frame" counter around a frame that runs only the measured
    /// section (an upper bound: other work in that frame adds to it; see <see cref="AllocationRounds"/>). A
    /// runtime with neither, or one where reading a counter kills the player (#414), skips the test instead of
    /// passing it vacuously (#426).
    /// </summary>
    /// <remarks>
    /// Used from a <c>[UnityTest]</c> on one thread: <c>yield return meter.Calibrate()</c>, then
    /// <c>yield return null</c>, <see cref="Begin"/>, the section, <see cref="End"/>, <c>yield return null</c>,
    /// and read <see cref="Allocated"/>.
    /// </remarks>
    internal sealed class AllocationMeter : IDisposable
    {
        private const string ThreadCounterName = "GC.GetAllocatedBytesForCurrentThread";
        private const string FrameRecorderName = "ProfilerRecorder(GC Allocated In Frame)";

        private Func<long> _threadCounter;
        private ProfilerRecorder _frameRecorder;
        private long _start;
        private long _threadAllocated;

        internal string Name { get; private set; } = "none";

        /// <summary>
        /// True when the per-thread counter measures: it counts only the section. False for the frame counter,
        /// which also counts the rest of the frame, on every thread.
        /// </summary>
        internal bool IsPrecise => _threadCounter != null;

        internal long Allocated => _threadCounter != null ? _threadAllocated : _frameRecorder.LastValue;

        internal IEnumerator Calibrate()
        {
            // Neither counter below may run here: the player would die instead of reporting (#414).
            var unsafeReason = ManagedAllocationCounter.UnsafeRuntimeReason;
            if (unsafeReason != null)
                TestRequirements.IgnoreEnvironment(unsafeReason);

            var counter = ManagedAllocationCounter.TryGetCountingThreadCounter();
            if (counter != null)
            {
                _threadCounter = counter;
                Name = ThreadCounterName;
                yield break;
            }

            ManagedAllocationCounter.LogRead(FrameRecorderName);
            _frameRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (!_frameRecorder.Valid)
                TestRequirements.IgnoreEnvironment("No managed allocation counter is available on this runtime.");

            yield return null;
            var frameProbe = new byte[ManagedAllocationCounter.ProbeBytes];
            GC.KeepAlive(frameProbe);
            yield return null;
            if (_frameRecorder.LastValue < ManagedAllocationCounter.ProbeBytes)
                TestRequirements.IgnoreEnvironment("The GC Allocated In Frame counter does not report allocations here.");

            Name = FrameRecorderName;
        }

        internal void Begin()
        {
            if (_threadCounter != null)
                _start = _threadCounter();
        }

        internal void End()
        {
            if (_threadCounter != null)
                _threadAllocated = _threadCounter() - _start;
        }

        public void Dispose()
        {
            if (_frameRecorder.Valid)
                _frameRecorder.Dispose();
        }
    }
}
