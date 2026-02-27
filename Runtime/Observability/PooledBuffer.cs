using System;
using System.Buffers;
using System.Threading;

#if DEBUG || TURBOHTTP_POOL_DIAGNOSTICS
using System.Diagnostics;
#endif

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Wraps an <see cref="IMemoryOwner{T}"/> with a length-sliced view and, in debug
    /// builds, guards against use-after-return and double-return defects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In release builds the wrapper is effectively zero-overhead: all guard fields
    /// are compiled away and the <see cref="Memory"/> property is a direct pass-through
    /// to the inner owner's memory sliced to the requested length.
    /// </para>
    /// <para>
    /// In <c>DEBUG</c> or <c>TURBOHTTP_POOL_DIAGNOSTICS</c> builds the wrapper:
    /// </para>
    /// <list type="bullet">
    ///   <item>Captures the allocation stack trace for diagnostics.</item>
    ///   <item>Throws <see cref="ObjectDisposedException"/> on any access after <see cref="Dispose"/>.</item>
    ///   <item>Throws <see cref="InvalidOperationException"/> on double-<see cref="Dispose"/>.</item>
    ///   <item>Enforces that <see cref="Memory"/> never exceeds the requested length.</item>
    /// </list>
    /// <para>
    /// <b>Thread safety:</b> <see cref="Dispose"/> is safe to call from any thread.
    /// However, callers must ensure no in-flight operation holds a <see cref="Memory"/>
    /// reference when <see cref="Dispose"/> is called. This is especially important
    /// for cancellation-token-triggered disposal of in-flight async network reads.
    /// </para>
    /// </remarks>
    public sealed class PooledBuffer<T> : IMemoryOwner<T>
    {
        // _owner == null means disposed. Interlocked.Exchange ensures atomic
        // first-dispose-wins semantics across threads.
        private IMemoryOwner<T> _owner;
        private readonly int _length;

#if DEBUG || TURBOHTTP_POOL_DIAGNOSTICS
        private readonly string _allocationStack;
#endif

        /// <summary>
        /// Wraps <paramref name="owner"/> exposing exactly <paramref name="length"/> elements.
        /// </summary>
        /// <param name="owner">The underlying memory owner. Must not be null.</param>
        /// <param name="length">Logical length. Must be ≥ 0 and ≤ owner.Memory.Length.</param>
        public PooledBuffer(IMemoryOwner<T> owner, int length)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (length < 0 || length > owner.Memory.Length)
                throw new ArgumentOutOfRangeException(nameof(length), length,
                    $"Length must be between 0 and the owner memory length ({owner.Memory.Length}).");

            _owner = owner;
            _length = length;

#if DEBUG || TURBOHTTP_POOL_DIAGNOSTICS
            // fNeedFileInfo: true provides file/line numbers in Editor and Mono builds.
            // Under IL2CPP (iOS/Android) file info may be absent even in development builds
            // because symbol data is stored in separate .sym files; method names are still
            // captured and are useful for diagnostics. Requires System.Diagnostics.StackTrace
            // to be preserved in link.xml when building with high IL2CPP stripping levels.
            _allocationStack = new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString();
#endif
        }

        /// <summary>
        /// Returns the logically usable slice of the underlying memory.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown in debug builds if the buffer has been returned.</exception>
        public Memory<T> Memory
        {
            get
            {
                var owner = Volatile.Read(ref _owner);
#if DEBUG || TURBOHTTP_POOL_DIAGNOSTICS
                if (owner == null)
                    throw new ObjectDisposedException(
                        nameof(PooledBuffer<T>),
                        $"Buffer was already returned to the pool. Allocation site:\n{_allocationStack}");
#endif
                return owner.Memory.Slice(0, _length);
            }
        }

        /// <summary>
        /// Returns the underlying buffer to the pool.
        /// First-dispose-wins: concurrent calls are safe — only the first caller disposes
        /// the inner owner; subsequent calls throw in debug builds or silently no-op in release.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown in <c>DEBUG</c>/<c>TURBOHTTP_POOL_DIAGNOSTICS</c> builds on double-return.
        /// </exception>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
            {
#if DEBUG || TURBOHTTP_POOL_DIAGNOSTICS
                throw new InvalidOperationException(
                    $"PooledBuffer<{typeof(T).Name}> was returned to the pool twice. " +
                    $"Allocation site:\n{_allocationStack}");
#else
                return;
#endif
            }

            owner.Dispose();
        }
    }
}
