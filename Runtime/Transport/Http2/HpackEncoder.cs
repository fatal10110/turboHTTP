using System;
using System.Buffers;
using System.Collections.Generic;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK header encoder. RFC 7541 Sections 6.1, 6.2, 6.3.
    /// NOT thread-safe — accessed only under Http2Connection._writeLock.
    /// </summary>
    internal class HpackEncoder
    {
        private readonly HpackDynamicTable _dynamicTable;
        private bool _pendingSizeUpdate;

        public HpackEncoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
        }

        /// <summary>
        /// Encode a list of headers into HPACK binary format.
        /// Headers should include pseudo-headers (e.g., :method, :path) first.
        /// </summary>
        public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers)
        {
            using var output = new PooledByteBuffer(EstimateInitialCapacity(headers));

            if (_pendingSizeUpdate)
            {
                EncodeInteger(_dynamicTable.MaxSize, 5, 0x20, output);
                _pendingSizeUpdate = false;
            }

            foreach (var (name, value) in headers)
            {
                if (IsSensitiveHeader(name))
                {
                    EncodeLiteralNeverIndexed(name, value, output);
                    continue;
                }

                var (index, match) = _dynamicTable.FindMatch(name, value);

                switch (match)
                {
                    case HpackMatchType.FullMatch:
                        EncodeInteger(index, 7, 0x80, output);
                        break;

                    case HpackMatchType.NameMatch:
                        EncodeInteger(index, 6, 0x40, output);
                        EncodeString(value, output);
                        _dynamicTable.Add(name, value);
                        break;

                    case HpackMatchType.None:
                        output.AddByte(0x40);
                        EncodeString(name, output);
                        EncodeString(value, output);
                        _dynamicTable.Add(name, value);
                        break;
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Emit a dynamic table size update instruction at the start of the next header block.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize)
        {
            _dynamicTable.SetMaxSize(newSize);
            _pendingSizeUpdate = true;
        }

        private static int EstimateInitialCapacity(IReadOnlyList<(string Name, string Value)> headers)
        {
            int capacity = 64;
            for (int i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                capacity += header.Name.Length + header.Value.Length + 8;
                if (capacity >= 8192)
                    return 8192;
            }
            return capacity;
        }

        private void EncodeLiteralNeverIndexed(string name, string value, PooledByteBuffer output)
        {
            var (index, match) = HpackStaticTable.FindMatch(name, value);

            if (match != HpackMatchType.None)
            {
                EncodeInteger(index, 4, 0x10, output);
            }
            else
            {
                output.AddByte(0x10);
                EncodeString(name, output);
            }
            EncodeString(value, output);
        }

        private static void EncodeString(string s, PooledByteBuffer output)
        {
            int rawLength = EncodingHelper.Latin1.GetByteCount(s);
            if (rawLength == 0)
            {
                EncodeInteger(0, 7, 0x00, output);
                return;
            }

            var raw = ArrayPool<byte>.Shared.Rent(rawLength);
            try
            {
                int bytesWritten = EncodingHelper.Latin1.GetBytes(s, 0, s.Length, raw, 0);
                int huffmanLength = HpackHuffman.GetEncodedLength(raw, 0, bytesWritten);

                if (huffmanLength < bytesWritten)
                {
                    byte[] huffmanEncoded = HpackHuffman.Encode(raw, 0, bytesWritten);
                    EncodeInteger(huffmanEncoded.Length, 7, 0x80, output);
                    output.AddRange(huffmanEncoded);
                }
                else
                {
                    EncodeInteger(bytesWritten, 7, 0x00, output);
                    output.AddRange(raw, bytesWritten);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }

        private static void EncodeInteger(int value, int prefixBits, byte prefixByte, PooledByteBuffer output)
        {
            int maxPrefix = (1 << prefixBits) - 1;

            if (value < maxPrefix)
            {
                output.AddByte((byte)(prefixByte | value));
            }
            else
            {
                output.AddByte((byte)(prefixByte | maxPrefix));
                value -= maxPrefix;
                while (value >= 128)
                {
                    output.AddByte((byte)((value & 0x7F) | 0x80));
                    value >>= 7;
                }
                output.AddByte((byte)value);
            }
        }

        private static bool IsSensitiveHeader(string name)
        {
            return string.Equals(name, "authorization", StringComparison.Ordinal)
                || string.Equals(name, "cookie", StringComparison.Ordinal)
                || string.Equals(name, "set-cookie", StringComparison.Ordinal)
                || string.Equals(name, "proxy-authorization", StringComparison.Ordinal);
        }

        private sealed class PooledByteBuffer : IDisposable
        {
            private byte[] _buffer;
            private int _length;
            private bool _disposed;

            public PooledByteBuffer(int initialCapacity)
            {
                if (initialCapacity < 1)
                    initialCapacity = 64;
                _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            }

            public void AddByte(byte value)
            {
                EnsureCapacity(1);
                _buffer[_length++] = value;
            }

            public void AddRange(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0)
                    return;

                EnsureCapacity(bytes.Length);
                Buffer.BlockCopy(bytes, 0, _buffer, _length, bytes.Length);
                _length += bytes.Length;
            }

            public void AddRange(byte[] bytes, int count)
            {
                if (bytes == null || count <= 0)
                    return;

                EnsureCapacity(count);
                Buffer.BlockCopy(bytes, 0, _buffer, _length, count);
                _length += count;
            }

            public byte[] ToArray()
            {
                var result = new byte[_length];
                if (_length > 0)
                    Buffer.BlockCopy(_buffer, 0, result, 0, _length);
                return result;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                var buffer = _buffer;
                _buffer = Array.Empty<byte>();
                _length = 0;
                if (buffer != null && buffer.Length > 0)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

            private void EnsureCapacity(int additional)
            {
                int required = _length + additional;
                if (required <= _buffer.Length)
                    return;

                int newSize = _buffer.Length;
                while (newSize < required)
                {
                    newSize = newSize < 1024 ? newSize * 2 : newSize + (newSize >> 1);
                }

                var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = newBuffer;
            }
        }
    }
}
