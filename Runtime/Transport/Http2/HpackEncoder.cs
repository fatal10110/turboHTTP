using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using TurboHTTP.Core.Internal;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK header encoder. RFC 7541 Sections 6.1, 6.2, 6.3.
    /// NOT thread-safe — accessed only under Http2Connection._writeLock, except
    /// <see cref="SetMaxDynamicTableSize"/>, which only records a pending change.
    /// Implements <see cref="IDisposable"/> so the reusable output buffer is returned
    /// to <see cref="ArrayPool{T}"/> when the encoder's owning connection is disposed.
    /// </summary>
    internal class HpackEncoder : IDisposable
    {
        private readonly HpackDynamicTable _dynamicTable;

        // Pending dynamic table size changes (RFC 7541 Section 4.2). Recorded by the connection read
        // loop, applied only inside Encode (under Http2Connection._writeLock) so the table is never
        // resized while a block is being encoded. Guarded by _sizeUpdateLock; _sizeUpdateSignal lets
        // Encode skip the lock when nothing changed.
        private readonly object _sizeUpdateLock = new object();
        private int _sizeUpdateSignal;
        private int _pendingMinSize;
        private int _pendingFinalSize;
        private int _appliedMaxSize;

        /// <summary>
        /// Reusable output buffer. Allocated once per encoder instance and reset
        /// (position set to 0, backing array retained) between <see cref="Encode"/> calls.
        /// This eliminates the per-call ArrayPool rent/return cycle for the output buffer.
        /// </summary>
        private readonly PooledArrayBufferWriter _outputBuffer = new PooledArrayBufferWriter(256);

        public HpackEncoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
            _appliedMaxSize = maxDynamicTableSize;
        }

        public void Dispose() => _outputBuffer.Dispose();

        /// <summary>
        /// Encode a list of headers into HPACK binary format.
        /// Headers should include pseudo-headers (e.g., :method, :path) first.
        /// </summary>
        /// <remarks>
        /// <b>Lifetime warning:</b> The returned <see cref="ReadOnlyMemory{T}"/> is a direct
        /// slice of this encoder's reusable output buffer. It is only valid until the next call
        /// to <see cref="Encode"/>. Callers MUST fully consume the data (e.g., await the frame
        /// write) before releasing the write lock that serialises <see cref="Encode"/> calls.
        /// Holding the slice across an <see cref="Encode"/> call will silently read garbage.
        /// </remarks>
        public ReadOnlyMemory<byte> Encode(IReadOnlyList<(string Name, string Value)> headers)
        {
            // Reset position to 0 — reuse the backing buffer rather than rent a new one.
            _outputBuffer.Reset();
            var output = _outputBuffer;

            if (Volatile.Read(ref _sizeUpdateSignal) != 0)
                ApplyPendingSizeUpdates(output);

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
                        AppendByte(output, 0x40);
                        EncodeString(name, output);
                        EncodeString(value, output);
                        _dynamicTable.Add(name, value);
                        break;
                }
            }

            return output.WrittenMemory;
        }

        /// <summary>
        /// Record a new maximum dynamic table size (the peer's SETTINGS_HEADER_TABLE_SIZE).
        /// Thread-safe: may be called from the read loop while another thread runs <see cref="Encode"/>.
        /// The table is resized, and the size update instructions emitted, at the start of the next
        /// <see cref="Encode"/> call: the smallest size recorded since the last block first, then the
        /// final size (RFC 7541 Section 4.2).
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize)
        {
            if (newSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newSize), newSize, "Table size must be non-negative.");

            lock (_sizeUpdateLock)
            {
                if (Volatile.Read(ref _sizeUpdateSignal) == 0)
                {
                    if (newSize == _appliedMaxSize)
                        return;

                    _pendingMinSize = newSize;
                }
                else if (newSize < _pendingMinSize)
                {
                    _pendingMinSize = newSize;
                }

                _pendingFinalSize = newSize;
                Volatile.Write(ref _sizeUpdateSignal, 1);
            }
        }

        private void ApplyPendingSizeUpdates(PooledArrayBufferWriter output)
        {
            int minSize;
            int finalSize;
            lock (_sizeUpdateLock)
            {
                if (Volatile.Read(ref _sizeUpdateSignal) == 0)
                    return;

                minSize = _pendingMinSize;
                finalSize = _pendingFinalSize;
                _appliedMaxSize = finalSize;
                Volatile.Write(ref _sizeUpdateSignal, 0);
            }

            // RFC 7541 Section 4.2: signal the smallest size since the last block (evicting down to
            // it, as the decoder will), then the final size. At most two updates.
            if (minSize < finalSize)
            {
                _dynamicTable.SetMaxSize(minSize);
                EncodeInteger(minSize, 5, 0x20, output);
            }

            _dynamicTable.SetMaxSize(finalSize);
            EncodeInteger(finalSize, 5, 0x20, output);
        }

        private void EncodeLiteralNeverIndexed(string name, string value, PooledArrayBufferWriter output)
        {
            var (index, match) = HpackStaticTable.FindMatch(name, value);

            if (match != HpackMatchType.None)
            {
                EncodeInteger(index, 4, 0x10, output);
            }
            else
            {
                AppendByte(output, 0x10);
                EncodeString(name, output);
            }
            EncodeString(value, output);
        }

        private static void EncodeString(string s, PooledArrayBufferWriter output)
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
                    // Encode Huffman directly into a pooled scratch buffer to avoid
                    // allocating an intermediate byte[] for each header value.
                    var huffmanBuf = ArrayPool<byte>.Shared.Rent(huffmanLength);
                    try
                    {
                        HpackHuffman.EncodeInto(raw, 0, bytesWritten, huffmanBuf, 0);
                        EncodeInteger(huffmanLength, 7, 0x80, output);
                        AppendRange(output, huffmanBuf, huffmanLength);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(huffmanBuf);
                    }
                }
                else
                {
                    EncodeInteger(bytesWritten, 7, 0x00, output);
                    AppendRange(output, raw, bytesWritten);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }

        private static void EncodeInteger(int value, int prefixBits, byte prefixByte, PooledArrayBufferWriter output)
        {
            int maxPrefix = (1 << prefixBits) - 1;

            if (value < maxPrefix)
            {
                AppendByte(output, (byte)(prefixByte | value));
            }
            else
            {
                AppendByte(output, (byte)(prefixByte | maxPrefix));
                value -= maxPrefix;
                while (value >= 128)
                {
                    AppendByte(output, (byte)((value & 0x7F) | 0x80));
                    value >>= 7;
                }
                AppendByte(output, (byte)value);
            }
        }

        private static void AppendByte(PooledArrayBufferWriter output, byte value)
        {
            var destination = output.GetSpan(1);
            destination[0] = value;
            output.Advance(1);
        }

        private static void AppendRange(PooledArrayBufferWriter output, byte[] source, int count)
        {
            if (source == null || count <= 0)
                return;

            source.AsSpan(0, count).CopyTo(output.GetSpan(count));
            output.Advance(count);
        }

        private static bool IsSensitiveHeader(string name)
        {
            return string.Equals(name, "authorization", StringComparison.Ordinal)
                || string.Equals(name, "cookie", StringComparison.Ordinal)
                || string.Equals(name, "set-cookie", StringComparison.Ordinal)
                || string.Equals(name, "proxy-authorization", StringComparison.Ordinal);
        }

    }
}
