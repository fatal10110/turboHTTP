// Core pooling infrastructure (Phase 19a refactor).
// Shared by Core consumers and friend assemblies (Transport/JSON/Files/WebSocket).

using System;
using System.Buffers;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// <see cref="IMemoryOwner{T}"/> backed by <see cref="ArrayPool{T}.Shared"/> with an
    /// explicit logical length. Dispose returns the rented array to the pool.
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

            return new ArrayPoolMemoryOwner<T>(ArrayPool<T>.Shared.Rent(length), length);
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

            ArrayPool<T>.Shared.Return(array);
        }
    }
}
