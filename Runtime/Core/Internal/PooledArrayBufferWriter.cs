// Core pooling infrastructure (Phase 19a refactor).
// Shared by Core consumers and friend assemblies (Transport/JSON/Files/WebSocket).

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{byte}.Shared"/>.
    /// Supports buffer growth and transfers ownership of written data via
    /// <see cref="DetachAsOwner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT thread-safe. Intended for use on a single thread or with external synchronization.
    /// </para>
    /// <para>
    /// Typical life cycle:
    /// <list type="number">
    /// <item>Acquire an instance from a pool or <c>new PooledArrayBufferWriter()</c>.</item>
    /// <item>Pass to a serializer that implements <see cref="IBufferWriter{byte}"/>.</item>
    /// <item>Either read the written data via <see cref="WrittenMemory"/> / <see cref="WrittenSpan"/>
    ///       and then call <see cref="Reset"/> to reuse, or call <see cref="DetachAsOwner"/> to
    ///       transfer ownership of the pooled array to the caller.</item>
    /// <item>Call <see cref="Dispose"/> when the writer is no longer needed (if ownership was
    ///       not transferred via <see cref="DetachAsOwner"/>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// After <see cref="DetachAsOwner"/> returns, the writer is in an unusable "detached" state.
    /// Attempting to call <see cref="GetSpan"/>, <see cref="GetMemory"/>, or <see cref="Advance"/>
    /// while detached throws <see cref="InvalidOperationException"/>. Call <see cref="Reset"/>
    /// to reinitialize for reuse.
    /// </para>
    /// </remarks>
    internal sealed class PooledArrayBufferWriter : IBufferWriter<byte>, IDisposable
    {
        /// <summary>Default initial capacity (256 bytes).</summary>
        private const int DefaultInitialCapacity = 256;

        /// <summary>Maximum size this writer will grow to (64 MiB).</summary>
        private const int MaxBufferSize = 64 * 1024 * 1024;

        private byte[] _buffer;
        private int _written;
        private bool _detached;

        // ── Construction / Factory ────────────────────────────────────────────────

        /// <summary>
        /// Initializes the writer with the default initial capacity (256 bytes).
        /// </summary>
        public PooledArrayBufferWriter() : this(DefaultInitialCapacity) { }

        /// <summary>
        /// Initializes the writer with at least <paramref name="initialCapacity"/> bytes.
        /// </summary>
        public PooledArrayBufferWriter(int initialCapacity)
        {
            if (initialCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                    "Initial capacity must be greater than zero.");

            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _written = 0;
            _detached = false;
        }

        // ── Written-data accessors ────────────────────────────────────────────────

        /// <summary>Number of bytes written so far.</summary>
        public int WrittenCount => _written;

        /// <summary>The bytes written so far as a <see cref="ReadOnlyMemory{T}"/>.</summary>
        public ReadOnlyMemory<byte> WrittenMemory
        {
            get
            {
                ThrowIfDetachedOrDisposed();
                return new ReadOnlyMemory<byte>(_buffer, 0, _written);
            }
        }

        /// <summary>The bytes written so far as a <see cref="ReadOnlySpan{T}"/>.</summary>
        public ReadOnlySpan<byte> WrittenSpan
        {
            get
            {
                ThrowIfDetachedOrDisposed();
                return new ReadOnlySpan<byte>(_buffer, 0, _written);
            }
        }

        // ── IBufferWriter<byte> ───────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Advance(int count)
        {
            ThrowIfDetachedOrDisposed();
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            if (_written + count > _buffer.Length)
                throw new InvalidOperationException(
                    $"Cannot advance by {count} bytes: only {_buffer.Length - _written} bytes available in current buffer.");

            _written += count;
        }

        /// <inheritdoc/>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ThrowIfDetachedOrDisposed();
            EnsureCapacity(sizeHint);
            return new Memory<byte>(_buffer, _written, _buffer.Length - _written);
        }

        /// <inheritdoc/>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ThrowIfDetachedOrDisposed();
            EnsureCapacity(sizeHint);
            return new Span<byte>(_buffer, _written, _buffer.Length - _written);
        }

        // ── Ownership transfer ────────────────────────────────────────────────────

        /// <summary>
        /// Transfers ownership of the written data to the caller as an
        /// <see cref="IMemoryOwner{byte}"/> sliced to the written length.
        /// After this call the writer enters a "detached" state and must be
        /// reinitialized via <see cref="Reset"/> before further use.
        /// </summary>
        /// <remarks>
        /// The returned owner's <see cref="IMemoryOwner{byte}.Memory"/> covers exactly
        /// the bytes written (not the full pool-allocated backing array).
        /// The caller is responsible for disposing the returned owner.
        /// </remarks>
        /// <returns>
        /// An <see cref="IMemoryOwner{byte}"/> wrapping the written data.
        /// </returns>
        public IMemoryOwner<byte> DetachAsOwner()
        {
            ThrowIfDetachedOrDisposed();

            var array = _buffer;
            int length = _written;

            _buffer = null;
            _written = 0;
            _detached = true;

            if (length == 0)
            {
                // Return the rented array immediately; hand back an empty owner.
                if (array != null && array.Length > 0)
                    ArrayPool<byte>.Shared.Return(array);
                return new ArrayPoolMemoryOwner<byte>(Array.Empty<byte>(), 0);
            }

            return new ArrayPoolMemoryOwner<byte>(array, length);
        }

        // ── Reuse / cleanup ───────────────────────────────────────────────────────

        /// <summary>
        /// Resets the writer for reuse. If in "detached" state, rents a new buffer.
        /// Written count is reset to zero; the backing buffer is NOT cleared.
        /// </summary>
        public void Reset(int newInitialCapacity = DefaultInitialCapacity)
        {
            if (_detached || _buffer == null)
            {
                // Re-acquire from pool.
                if (newInitialCapacity <= 0)
                    newInitialCapacity = DefaultInitialCapacity;
                _buffer = ArrayPool<byte>.Shared.Rent(newInitialCapacity);
                _detached = false;
            }
            _written = 0;
        }

        /// <summary>
        /// Returns the backing array to the pool. After disposal the writer must not be used.
        /// If ownership was already transferred via <see cref="DetachAsOwner"/>, this is a no-op.
        /// </summary>
        public void Dispose()
        {
            if (_detached)
                return;

            var buffer = _buffer;
            _buffer = null;
            _written = 0;
            _detached = true; // treat disposed as unusable

            if (buffer != null && buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        private void EnsureCapacity(int sizeHint)
        {
            int needed = sizeHint <= 0 ? 1 : sizeHint;
            int available = _buffer.Length - _written;

            if (available >= needed)
                return;

            int required = checked(_written + needed);
            if (required > MaxBufferSize)
                throw new InvalidOperationException(
                    $"Buffer growth would exceed maximum size of {MaxBufferSize} bytes. " +
                    $"Requested: {required} bytes.");

            int newSize = _buffer.Length;
            while (newSize < required)
            {
                // Double up to 1 MiB, then grow by 50% to limit over-allocation on large bodies.
                newSize = newSize < 1024 * 1024 ? newSize * 2 : newSize + (newSize >> 1);
                if (newSize > MaxBufferSize)
                {
                    newSize = MaxBufferSize;
                    break;
                }
            }

            var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            if (_written > 0)
                Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _written);

            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDetachedOrDisposed()
        {
            if (_detached || _buffer == null)
                throw new InvalidOperationException(
                    "PooledArrayBufferWriter is in an unusable state. " +
                    "Either it has been disposed or DetachAsOwner() was called. " +
                    "Call Reset() to reinitialize for reuse.");
        }
    }
}
