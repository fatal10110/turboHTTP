using System;
using System.Diagnostics;
using System.Reflection;
using TurboHTTP.Core;

namespace TurboHTTP.Tests
{
    /// <summary>The runtime and counter an allocation budget applies to (#438).</summary>
    public enum AllocationProfile
    {
        /// <summary>A per-thread counter that counts exactly the section.</summary>
        Precise,

        /// <summary>
        /// The frame counter over optimized code: the host editor, which compiles the package with the
        /// optimizer on.
        /// </summary>
        OptimizedFrame,

        /// <summary>
        /// The frame counter over Mono code compiled without optimization: Test Runner players are development
        /// builds, where every <c>async</c> method allocates its state machine even when it completes
        /// synchronously.
        /// </summary>
        UnoptimizedMonoFrame,

        /// <summary>The frame counter over IL2CPP code converted from IL compiled without optimization.</summary>
        UnoptimizedIl2CppFrame,
    }

    /// <summary>
    /// Bytes per unit one allocation gate allows on each <see cref="AllocationProfile"/>. A profile's budget is
    /// the measured cost on that profile with a small margin, never a blanket widening: the issue that owns
    /// the allocation is cited where a budget is above the others.
    /// </summary>
    internal readonly struct AllocationBudget
    {
        internal AllocationBudget(
            long precise,
            long optimizedFrame,
            long unoptimizedMonoFrame,
            long unoptimizedIl2CppFrame)
        {
            Precise = precise;
            OptimizedFrame = optimizedFrame;
            UnoptimizedMonoFrame = unoptimizedMonoFrame;
            UnoptimizedIl2CppFrame = unoptimizedIl2CppFrame;
        }

        internal long Precise { get; }
        internal long OptimizedFrame { get; }
        internal long UnoptimizedMonoFrame { get; }
        internal long UnoptimizedIl2CppFrame { get; }

        internal long For(AllocationProfile profile)
        {
            switch (profile)
            {
                case AllocationProfile.Precise:
                    return Precise;
                case AllocationProfile.OptimizedFrame:
                    return OptimizedFrame;
                case AllocationProfile.UnoptimizedMonoFrame:
                    return UnoptimizedMonoFrame;
                case AllocationProfile.UnoptimizedIl2CppFrame:
                    return UnoptimizedIl2CppFrame;
                default:
                    throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
            }
        }

        /// <summary>The profile for a meter with or without a precise counter on the running player or editor.</summary>
        internal static AllocationProfile CurrentProfile(bool preciseCounter)
        {
            return SelectProfile(preciseCounter, IsCodeUnderTestOptimized(), IsIl2Cpp);
        }

        internal static AllocationProfile SelectProfile(bool preciseCounter, bool optimized, bool il2cpp)
        {
            if (preciseCounter)
                return AllocationProfile.Precise;
            if (optimized)
                return AllocationProfile.OptimizedFrame;

            return il2cpp ? AllocationProfile.UnoptimizedIl2CppFrame : AllocationProfile.UnoptimizedMonoFrame;
        }

        /// <summary>
        /// False when the package's assemblies were compiled without optimization (the compiler marks them
        /// with <see cref="DebuggableAttribute"/> and the JIT optimizer disabled).
        /// </summary>
        internal static bool IsCodeUnderTestOptimized()
        {
            var debuggable = typeof(UHttpClient).Assembly.GetCustomAttribute<DebuggableAttribute>();
            return debuggable == null || !debuggable.IsJITOptimizerDisabled;
        }

        private static bool IsIl2Cpp
        {
            get
            {
#if ENABLE_IL2CPP && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }
    }
}
