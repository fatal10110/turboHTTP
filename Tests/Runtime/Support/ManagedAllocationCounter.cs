using System;
using System.Reflection;
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
        /// players and Unity 2021.3 Android IL2CPP players read the counter and pass.
        /// </summary>
        internal static string UnsafeRuntimeReason
        {
            get
            {
#if UNITY_IOS && ENABLE_IL2CPP && !UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
                return "Reading a managed allocation counter kills Unity 2021.3 IL2CPP iOS players with SIGILL (#414).";
#else
                return null;
#endif
            }
        }

        /// <summary>
        /// <c>GC.GetAllocatedBytesForCurrentThread</c>, resolved by reflection because the .NET Standard 2.1
        /// profile does not expose it. Returns null where the method is missing or
        /// <see cref="UnsafeRuntimeReason"/> applies. Callers still check that it counts: Unity's Mono
        /// returns 0.
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
        /// Logs the counter a test is about to read, so a player log that ends in a crash names the counter
        /// the crashing test used (#414).
        /// </summary>
        internal static void LogRead(string counterName)
        {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "TURBOHTTP_ALLOCATION_COUNTER reading {0}", counterName);
        }
    }
}
