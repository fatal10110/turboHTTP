// Core pooling infrastructure (Phase 19a refactor).
// Shared by Core consumers and friend assemblies (Transport/JSON/Files/WebSocket).

using System;
using System.Buffers;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// <see cref="IMemoryOwner{T}"/> backed by <see cref="ArrayPool{T}.Shared"/> with an
    /// explicit logical length. Dispose returns the rented array to the pool. Byte arrays are
    /// rented and returned through <see cref="ByteArrayPool"/> so they are counted; a byte array
    /// handed to the constructor must therefore come from <see cref="ByteArrayPool.Rent"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="Memory"/> property is sliced to the requested logical length so
    /// callers never see padding bytes from pool over-allocation.
    /// </remarks>
    internal sealed class ArrayPoolMemoryOwner<T> : IMemoryOwner<T>
    {
        private T[] _array;
        private int _length;

        public ArrayPoolMemoryOwner(T[] array, int length)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if (length < 0 || length > array.Length)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be in range [0, array.Length].");

            _array = array;
            _length = length;
        }

        /// <summary>
        /// Rents an array of at least <paramref name="length"/> elements from
        /// <see cref="ArrayPool{T}.Shared"/> and wraps it as an owner.
        /// A length of 0 returns a zero-length owner backed by <see cref="Array.Empty{T}"/>
        /// without touching the pool.
        /// </summary>
        public static ArrayPoolMemoryOwner<T> Rent(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Length cannot be negative.");

            if (length == 0)
                return new ArrayPoolMemoryOwner<T>(Array.Empty<T>(), 0);

            // Byte arrays go through the counting ByteArrayPool so the leak oracle (#292) sees them;
            // the typeof check is a JIT/IL2CPP constant per instantiation and the casts are reference casts.
            T[] array = typeof(T) == typeof(byte)
                ? (T[])(object)ByteArrayPool.Rent(length)
                : ArrayPool<T>.Shared.Rent(length);
            return new ArrayPoolMemoryOwner<T>(array, length);
        }

        /// <summary>
        /// Returns the logically usable slice of the rented array.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The owner has been disposed.</exception>
        public Memory<T> Memory
        {
            get
            {
                var array = _array;
                if (array == null)
                    throw new ObjectDisposedException(nameof(ArrayPoolMemoryOwner<T>));

                if (_length == 0)
                    return Memory<T>.Empty;

                return new Memory<T>(array, 0, _length);
            }
        }

        /// <summary>
        /// Transfers ownership of the underlying array out of this owner.
        /// After detach, <see cref="Dispose"/> becomes a no-op.
        /// </summary>
        /// <returns><c>true</c> if detach succeeded; <c>false</c> if already disposed or detached.</returns>
        public bool TryDetach(out T[] array, out int length)
        {
            if (_array == null)
            {
                array = null;
                length = 0;
                return false;
            }

            array = _array;
            length = _length;
            _array = null;
            _length = 0;
            return true;
        }

        /// <summary>
        /// Returns the underlying array to <see cref="ArrayPool{T}.Shared"/>.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        public void Dispose()
        {
            var array = _array;
            _array = null;
            _length = 0;

            if (array == null || array.Length == 0)
                return;

            if (typeof(T) == typeof(byte))
                ByteArrayPool.Return((byte[])(object)array);
            else
                ArrayPool<T>.Shared.Return(array);
        }
    }
}
