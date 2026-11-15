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
        private int _peerMaxSize;

        /// <summary>
        /// Default upper bound on this encoder's own dynamic table, whatever the peer's
        /// SETTINGS_HEADER_TABLE_SIZE allows (RFC 7541 Section 4.2: the encoder may use any size up
        /// to the peer's limit). Equal to the HTTP/2 initial value: a peer that raises its limit gets an
        /// update carrying 4096, and the table never grows past it.
        /// </summary>
        internal const int DefaultLocalTableSizeCap = 4096;

        // Upper bound on the table size this encoder uses; every peer value is clamped to it so the
        // memory the table holds and the per-header FindMatch scan stay bounded (RFC 7541 Section 7.3).
        private readonly int _localTableSizeCap;

        /// <summary>
        /// Reusable output buffer. Allocated once per encoder instance and reset
        /// (position set to 0, backing array retained) between <see cref="Encode"/> calls.
        /// This eliminates the per-call ArrayPool rent/return cycle for the output buffer.
        /// </summary>
        private readonly PooledArrayBufferWriter _outputBuffer = new PooledArrayBufferWriter(256);

        /// <param name="maxDynamicTableSize">The peer's table size limit at connection start (the HTTP/2
        /// initial SETTINGS_HEADER_TABLE_SIZE, 4096).</param>
        /// <param name="localTableSizeCap">Upper bound on the table size this encoder uses. When it is
        /// below <paramref name="maxDynamicTableSize"/> the first block signals the smaller size.</param>
        public HpackEncoder(int maxDynamicTableSize = 4096, int localTableSizeCap = DefaultLocalTableSizeCap)
        {
            if (maxDynamicTableSize < 0)
                throw new ArgumentOutOfRangeException(nameof(maxDynamicTableSize), maxDynamicTableSize, "Table size must be non-negative.");
            if (localTableSizeCap < 0)
                throw new ArgumentOutOfRangeException(nameof(localTableSizeCap), localTableSizeCap, "Table size cap must be non-negative.");

            _localTableSizeCap = localTableSizeCap;
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
            _peerMaxSize = maxDynamicTableSize;

            // The peer's decoder starts at maxDynamicTableSize; using less must be signalled in the
            // first block (RFC 7541 Section 4.2).
            if (localTableSizeCap < maxDynamicTableSize)
            {
                _pendingMinSize = localTableSizeCap;
                _pendingFinalSize = localTableSizeCap;
                _sizeUpdateSignal = 1;
            }
        }

        /// <summary>Current size of this encoder's dynamic table in RFC 7541 Section 4.1 octets.</summary>
        internal int DynamicTableSize => _dynamicTable.CurrentSize;

        /// <summary>Maximum size of this encoder's dynamic table as last applied by <see cref="Encode"/>.</summary>
        internal int DynamicTableMaxSize => _dynamicTable.MaxSize;

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
        /// final size (RFC 7541 Section 4.2). The size used is the peer's value clamped to the local
        /// cap, so a peer advertising up to 2^31-1 cannot grow the table past it. A changed peer value
        /// is always answered with an update carrying the clamped size, even when that equals the
        /// size in use (legal, and accepted by decoders that expect an update after every change); a
        /// repeated peer value emits nothing.
        /// </summary>
        public void SetMaxDynamicTableSize(int newSize)
        {
            if (newSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newSize), newSize, "Table size must be non-negative.");

            int cappedSize = Math.Min(newSize, _localTableSizeCap);

            lock (_sizeUpdateLock)
            {
                if (Volatile.Read(ref _sizeUpdateSignal) == 0)
                {
                    if (newSize == _peerMaxSize)
                        return;

                    _pendingMinSize = cappedSize;
                }
                else if (cappedSize < _pendingMinSize)
                {
                    _pendingMinSize = cappedSize;
                }

                _peerMaxSize = newSize;
                _pendingFinalSize = cappedSize;
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

            var raw = ByteArrayPool.Rent(rawLength);
            try
            {
                int bytesWritten = EncodingHelper.Latin1.GetBytes(s, 0, s.Length, raw, 0);
                int huffmanLength = HpackHuffman.GetEncodedLength(raw, 0, bytesWritten);

                if (huffmanLength < bytesWritten)
                {
                    // Encode Huffman directly into a pooled scratch buffer to avoid
                    // allocating an intermediate byte[] for each header value.
                    var huffmanBuf = ByteArrayPool.Rent(huffmanLength);
                    try
                    {
                        HpackHuffman.EncodeInto(raw, 0, bytesWritten, huffmanBuf, 0);
                        EncodeInteger(huffmanLength, 7, 0x80, output);
                        AppendRange(output, huffmanBuf, huffmanLength);
                    }
                    finally
                    {
                        ByteArrayPool.Return(huffmanBuf);
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
                ByteArrayPool.Return(raw);
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
