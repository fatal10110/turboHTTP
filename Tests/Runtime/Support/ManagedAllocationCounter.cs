using System;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// The managed allocation counters the allocation tests read, and the runtimes where none of them may be
    /// called (#414).
    /// </summary>
    internal static class ManagedAllocationCounter
    {
        /// <summary>
        /// Why this runtime must not call an allocation counter, or null where it may. A Unity 2021.3 IL2CPP
        /// iOS player (the x86_64 simulator player under Rosetta 2) dies with SIGILL in the first test that
        /// reads one (#414). A signal cannot be caught in managed code, so the counter is never called
        /// there: neither <c>GC.GetAllocatedBytesForCurrentThread</c> nor the profiler's GC counters. A test
        /// that has no other counter skips with this reason instead of taking the player down. Unity 6 iOS
        /// players and Unity 2021.3 Android IL2CPP players read the counter and pass. The guard covers 2021.3
        /// only, the version the crash was seen on; 2022.x and 2023.x are untested.
        /// </summary>
        internal static string UnsafeRuntimeReason
        {
            get
            {
#if UNITY_IOS && ENABLE_IL2CPP && !UNITY_EDITOR && !UNITY_2022_1_OR_NEWER
                return "Reading a managed allocation counter kills Unity 2021.3 IL2CPP iOS players with SIGILL (#414).";
#else
                return null;
#endif
            }
        }

        /// <summary>The size of the block a probe allocates; a counter that counts sees at least this much.</summary>
        internal const int ProbeBytes = 512 * 1024;

        /// <summary>The size of the small blocks <see cref="ResolvesSmallAllocations"/> allocates.</summary>
        internal const int SmallProbeBytes = 64;

        /// <summary>How many small blocks <see cref="ResolvesSmallAllocations"/> allocates, one per reading.</summary>
        internal const int SmallProbeCount = 8;

        /// <summary>The most a counter may add to a small block for the array's header and alignment.</summary>
        internal const int ObjectHeaderAllowanceBytes = 64;

        /// <summary>
        /// <c>GC.GetAllocatedBytesForCurrentThread</c>, resolved by reflection because the .NET Standard 2.1
        /// profile does not expose it. Returns null where the method is missing or
        /// <see cref="UnsafeRuntimeReason"/> applies. It does not check that the counter counts: Unity's Mono
        /// (Boehm GC) has the method and always returns 0. Measure with
        /// <see cref="TryGetCountingThreadCounter"/> instead.
        /// </summary>
        internal static Func<long> TryGetThreadCounter()
        {
            if (UnsafeRuntimeReason != null)
                return null;

            var api = typeof(GC).GetMethod(
                "GetAllocatedBytesForCurrentThread",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (api == null || api.ReturnType != typeof(long))
                return null;

            return (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), api);
        }

        /// <summary>
        /// The per-thread counter where it measures exactly what the calling thread allocates, else null: where
        /// <see cref="TryGetThreadCounter"/> has none, where it does not count a known allocation (Unity's Mono
        /// reads 0), where it misses or rounds a small one, or where it also counts another thread. Unity
        /// 2021.3's IL2CPP has the last two: the reading moves in 4 KiB steps, from 0 to several hundred bytes
        /// per read for the same work, and includes other threads (#438). The counter is per thread, so call
        /// this on the thread that measures, not from a static initializer.
        /// </summary>
        internal static Func<long> TryGetCountingThreadCounter()
        {
            var counter = TryGetThreadCounter();
            if (counter == null)
                return null;

            LogRead("GC.GetAllocatedBytesForCurrentThread");
            return Counts(counter) && ResolvesSmallAllocations(counter) && !SeesOtherThreads(counter)
                ? counter
                : null;
        }

        /// <summary>
        /// True when <paramref name="counter"/> grows by at least <see cref="ProbeBytes"/> across an allocation
        /// of that size on the calling thread.
        /// </summary>
        internal static bool Counts(Func<long> counter)
        {
            if (counter == null)
                throw new ArgumentNullException(nameof(counter));

            var before = counter();
            var probe = new byte[ProbeBytes];
            var delta = counter() - before;
            GC.KeepAlive(probe);
            return delta >= ProbeBytes;
        }

        /// <summary>
        /// True when <paramref name="counter"/> reads every one of several <see cref="SmallProbeBytes"/>-byte
        /// allocations as that size plus at most <see cref="ObjectHeaderAllowanceBytes"/> of object header. A
        /// counter that moves in pages reads 0 or a page instead.
        /// </summary>
        internal static bool ResolvesSmallAllocations(Func<long> counter)
        {
            if (counter == null)
                throw new ArgumentNullException(nameof(counter));

            for (var i = 0; i < SmallProbeCount; i++)
            {
                var before = counter();
                var probe = new byte[SmallProbeBytes];
                var delta = counter() - before;
                GC.KeepAlive(probe);
                if (delta < SmallProbeBytes || delta > SmallProbeBytes + ObjectHeaderAllowanceBytes)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="counter"/>, read on the calling thread, grows by at least
        /// <see cref="ProbeBytes"/> while another thread allocates that much: it counts more than the thread.
        /// </summary>
        internal static bool SeesOtherThreads(Func<long> counter)
        {
            if (counter == null)
                throw new ArgumentNullException(nameof(counter));

            var before = counter();
            var other = new Thread(() => GC.KeepAlive(new byte[ProbeBytes]));
            other.Start();
            other.Join();
            return counter() - before >= ProbeBytes;
        }

        /// <summary>
        /// Logs the counter a test is about to read, so a player log that ends in a crash names the counter
        /// the crashing test used (#414).
        /// </summary>
        internal static void LogRead(string counterName)
        {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "TURBOHTTP_ALLOCATION_COUNTER reading {0}", counterName);
        }
    }
}
