using System;
using System.Buffers;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// ArrayPool-backed IMemoryOwner implementation with explicit logical length.
    /// </summary>
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

        public static ArrayPoolMemoryOwner<T> Rent(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Length cannot be negative.");

            if (length == 0)
            {
                return new ArrayPoolMemoryOwner<T>(Array.Empty<T>(), 0);
            }

            return new ArrayPoolMemoryOwner<T>(ArrayPool<T>.Shared.Rent(length), length);
        }

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
