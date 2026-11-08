using System;
using System.Buffers;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Managed raw DEFLATE encoder (RFC 1951) whose output always ends at a sync-flush boundary, with the
    /// trailing <c>00 00 FF FF</c> of the empty stored block removed (RFC 7692 section 7.2.1). It never
    /// writes a final block (BFINAL=1), so a server that inflates every message with one retained stream
    /// (no <c>client_no_context_takeover</c>, RFC 7692 section 7.2.2) reads all messages. It exists for
    /// runtimes whose <see cref="System.IO.Compression.DeflateStream"/> cannot sync-flush (Unity Mono, #270).
    /// </summary>
    /// <remarks>
    /// Output is fixed-Huffman blocks (RFC 1951 section 3.2.6) with greedy LZ77 matching, or stored blocks
    /// when those are not larger, so a message grows by at most 5 bytes per 65535 plus one. Matches stay
    /// inside the message and within the 32768-byte window a 15-bit inflater keeps (client_max_window_bits
    /// is never offered). Work per position is bounded by the chain length the compression level picks;
    /// all scratch arrays come from <see cref="ArrayPool{T}.Shared"/>; only the returned owner keeps one.
    /// </remarks>
    internal static class SyncFlushDeflater
    {
        internal const int MaxDistance = 32768;
        internal const int MaxStoredBlockLength = 65535;

        private const int MinMatch = 3;
        private const int MaxMatch = 258;
        private const int MinWindowBits = 8;
        private const int MaxWindowBits = 15;
        private const int EndOfBlock = 256;
        private const int OutputSlack = 8;

        private static readonly ushort[] LengthBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131,
            163, 195, 227, 258,
        };

        private static readonly byte[] LengthExtraBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
        };

        private static readonly ushort[] DistanceBase =
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049,
            3073, 4097, 6145, 8193, 12289, 16385, 24577,
        };

        private static readonly byte[] DistanceExtraBits =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
        };

        // Fixed literal/length and distance codes (RFC 1951 section 3.2.6), bit-reversed because Huffman
        // codes are packed most-significant bit first into an LSB-first bit stream (section 3.1.1).
        private static readonly byte[] LiteralCodeLengths = BuildLiteralCodeLengths();
        private static readonly ushort[] LiteralCodes = BuildLiteralCodes();
        private static readonly byte[] DistanceCodes = BuildDistanceCodes();

        // LengthIndex[length - 3] is the length code minus 257; DistanceIndex is read by DistanceCode.
        private static readonly byte[] LengthIndex = BuildLengthIndex();
        private static readonly byte[] DistanceIndex = BuildDistanceIndex();

        /// <summary>
        /// Compresses one message. The caller owns the returned buffer and disposes it once.
        /// </summary>
        /// <param name="input">The whole message payload.</param>
        /// <param name="compressionLevel">0 writes stored blocks only; 1-9 search longer match chains.</param>
        public static ArrayPoolMemoryOwner<byte> Compress(ReadOnlySpan<byte> input, int compressionLevel)
        {
            int storedLength = StoredLength(input.Length);
            byte[] output = ByteArrayPool.Rent(storedLength + OutputSlack);
            try
            {
                int written = -1;
                if (compressionLevel > 0 && input.Length > 0)
                    written = EncodeFixedHuffman(input, output, storedLength, MaxChainLength(compressionLevel));
                if (written < 0)
                    written = EncodeStored(input, output);

                // The scratch buffer is sized for the stored form; give a well-compressed message its
                // own small buffer so the large one goes back to the pool before the send.
                if (written <= output.Length / 2)
                {
                    var compact = ArrayPoolMemoryOwner<byte>.Rent(written);
                    output.AsSpan(0, written).CopyTo(compact.Memory.Span);
                    return compact;
                }

                var owner = new ArrayPoolMemoryOwner<byte>(output, written);
                output = null;
                return owner;
            }
            finally
            {
                if (output != null)
                    ByteArrayPool.Return(output);
            }
        }

        /// <summary>Length of the stored-block encoding: 5 header bytes per block plus the flush byte.</summary>
        internal static int StoredLength(int inputLength)
        {
            long blocks = ((long)inputLength + MaxStoredBlockLength - 1) / MaxStoredBlockLength;
            long length = inputLength + (5 * blocks) + 1;
            if (length > int.MaxValue - OutputSlack)
                throw new InvalidOperationException("The message is too large to compress.");
            return (int)length;
        }

        private static int MaxChainLength(int compressionLevel)
        {
            if (compressionLevel <= 3)
                return 8;
            if (compressionLevel <= 6)
                return 32;
            return 128;
        }

        private static int EncodeStored(ReadOnlySpan<byte> input, byte[] output)
        {
            int position = 0;
            int offset = 0;
            while (offset < input.Length)
            {
                int blockLength = Math.Min(MaxStoredBlockLength, input.Length - offset);

                // BFINAL=0, BTYPE=00, then padding to the byte boundary; LEN and NLEN little-endian.
                output[position++] = 0x00;
                output[position++] = (byte)blockLength;
                output[position++] = (byte)(blockLength >> 8);
                output[position++] = (byte)~blockLength;
                output[position++] = (byte)(~blockLength >> 8);
                input.Slice(offset, blockLength).CopyTo(output.AsSpan(position));
                position += blockLength;
                offset += blockLength;
            }

            // The sync flush: an empty stored block whose LEN/NLEN (00 00 FF FF) is removed.
            output[position++] = 0x00;
            return position;
        }

        /// <summary>
        /// Writes one fixed-Huffman block and the sync flush. Returns -1 once the output would not be
        /// smaller than <paramref name="limit"/> (the stored encoding), so the caller writes that instead.
        /// </summary>
        private static int EncodeFixedHuffman(ReadOnlySpan<byte> input, byte[] output, int limit, int maxChainLength)
        {
            int length = input.Length;
            int windowBits = MinWindowBits;
            while (windowBits < MaxWindowBits && (1 << windowBits) < length)
                windowBits++;

            int tableSize = 1 << windowBits;
            int windowMask = tableSize - 1;
            int hashShift = 32 - windowBits;

            int[] head = ArrayPool<int>.Shared.Rent(tableSize);
            int[] previous = ArrayPool<int>.Shared.Rent(tableSize);
            try
            {
                head.AsSpan(0, tableSize).Fill(-1);

                var writer = new BitWriter(output);

                // BFINAL=0, BTYPE=01 (fixed Huffman).
                writer.Write(0b010, 3);

                int position = 0;
                while (position < length)
                {
                    int bestLength = 0;
                    int bestDistance = 0;

                    if (position + MinMatch <= length)
                    {
                        int hash = Hash(input, position, hashShift);
                        int maxLength = Math.Min(MaxMatch, length - position);
                        int candidate = head[hash];
                        int chain = maxChainLength;

                        while (candidate >= 0 && chain-- > 0)
                        {
                            int distance = position - candidate;
                            if (distance > MaxDistance)
                                break;

                            // bestLength < maxLength here, so both indexes are inside the input.
                            if (input[candidate + bestLength] == input[position + bestLength])
                            {
                                int matchLength = 0;
                                while (matchLength < maxLength && input[candidate + matchLength] == input[position + matchLength])
                                    matchLength++;

                                if (matchLength > bestLength)
                                {
                                    bestLength = matchLength;
                                    bestDistance = distance;
                                    if (matchLength == maxLength)
                                        break;
                                }
                            }

                            // Chains only go back in the input; a slot reused by a newer position ends it.
                            int next = previous[candidate & windowMask];
                            if (next >= candidate)
                                break;
                            candidate = next;
                        }

                        previous[position & windowMask] = head[hash];
                        head[hash] = position;
                    }

                    if (bestLength >= MinMatch)
                    {
                        writer.WriteMatch(bestLength, bestDistance);

                        int end = position + bestLength;
                        for (int inserted = position + 1; inserted < end && inserted + MinMatch <= length; inserted++)
                        {
                            int hash = Hash(input, inserted, hashShift);
                            previous[inserted & windowMask] = head[hash];
                            head[hash] = inserted;
                        }

                        position = end;
                    }
                    else
                    {
                        writer.WriteLiteral(input[position]);
                        position++;
                    }

                    // One token adds at most 31 bits, so the output never passes limit + OutputSlack.
                    if (writer.Position >= limit)
                        return -1;
                }

                writer.Write(LiteralCodes[EndOfBlock], LiteralCodeLengths[EndOfBlock]);

                // The sync flush: an empty stored block (BFINAL=0, BTYPE=00) padded to the byte boundary;
                // its LEN/NLEN (00 00 FF FF) is never written (RFC 7692 section 7.2.1 step 3).
                writer.Write(0, 3);
                writer.AlignToByte();

                return writer.Position >= limit ? -1 : writer.Position;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(head);
                ArrayPool<int>.Shared.Return(previous);
            }
        }

        private static int Hash(ReadOnlySpan<byte> input, int position, int hashShift)
        {
            uint key = (uint)(input[position] | (input[position + 1] << 8) | (input[position + 2] << 16));
            return (int)((key * 2654435761u) >> hashShift);
        }

        private static int DistanceCode(int distance)
        {
            int value = distance - 1;
            return value < 256 ? DistanceIndex[value] : DistanceIndex[256 + (value >> 7)];
        }

        private static int Reverse(int code, int bitCount)
        {
            int reversed = 0;
            for (int i = 0; i < bitCount; i++)
            {
                reversed = (reversed << 1) | (code & 1);
                code >>= 1;
            }

            return reversed;
        }

        private static byte[] BuildLiteralCodeLengths()
        {
            var lengths = new byte[288];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (symbol < 144)
                    lengths[symbol] = 8;
                else if (symbol < 256)
                    lengths[symbol] = 9;
                else if (symbol < 280)
                    lengths[symbol] = 7;
                else
                    lengths[symbol] = 8;
            }

            return lengths;
        }

        private static ushort[] BuildLiteralCodes()
        {
            var codes = new ushort[288];
            for (int symbol = 0; symbol < codes.Length; symbol++)
            {
                int code;
                int bitCount;
                if (symbol < 144)
                {
                    code = 0x30 + symbol;
                    bitCount = 8;
                }
                else if (symbol < 256)
                {
                    code = 0x190 + (symbol - 144);
                    bitCount = 9;
                }
                else if (symbol < 280)
                {
                    code = symbol - 256;
                    bitCount = 7;
                }
                else
                {
                    code = 0xC0 + (symbol - 280);
                    bitCount = 8;
                }

                codes[symbol] = (ushort)Reverse(code, bitCount);
            }

            return codes;
        }

        private static byte[] BuildDistanceCodes()
        {
            var codes = new byte[30];
            for (int code = 0; code < codes.Length; code++)
                codes[code] = (byte)Reverse(code, 5);
            return codes;
        }

        private static byte[] BuildLengthIndex()
        {
            var index = new byte[MaxMatch - MinMatch + 1];
            for (int code = 0; code < LengthBase.Length; code++)
            {
                int last = code == LengthBase.Length - 1 ? MaxMatch : LengthBase[code + 1] - 1;
                for (int matchLength = LengthBase[code]; matchLength <= last; matchLength++)
                    index[matchLength - MinMatch] = (byte)code;
            }

            return index;
        }

        private static byte[] BuildDistanceIndex()
        {
            // Distances up to 256 map directly; above that every code starts at a multiple of 128 + 1,
            // so (distance - 1) >> 7 selects the code (the zlib layout).
            var index = new byte[512];
            for (int code = 0; code < DistanceBase.Length; code++)
            {
                int last = code == DistanceBase.Length - 1 ? MaxDistance : DistanceBase[code + 1] - 1;
                for (int distance = DistanceBase[code]; distance <= last; distance++)
                {
                    int value = distance - 1;
                    index[value < 256 ? value : 256 + (value >> 7)] = (byte)code;
                }
            }

            return index;
        }

        private struct BitWriter
        {
            private readonly byte[] _buffer;
            private ulong _bits;
            private int _bitCount;

            public BitWriter(byte[] buffer)
            {
                _buffer = buffer;
                _bits = 0;
                _bitCount = 0;
                Position = 0;
            }

            public int Position { get; private set; }

            public void Write(uint value, int bitCount)
            {
                _bits |= (ulong)value << _bitCount;
                _bitCount += bitCount;
                while (_bitCount >= 8)
                {
                    _buffer[Position++] = (byte)_bits;
                    _bits >>= 8;
                    _bitCount -= 8;
                }
            }

            public void WriteLiteral(byte value)
            {
                Write(LiteralCodes[value], LiteralCodeLengths[value]);
            }

            public void WriteMatch(int matchLength, int distance)
            {
                int lengthCode = LengthIndex[matchLength - MinMatch];
                int symbol = EndOfBlock + 1 + lengthCode;
                Write(LiteralCodes[symbol], LiteralCodeLengths[symbol]);
                int lengthExtra = LengthExtraBits[lengthCode];
                if (lengthExtra > 0)
                    Write((uint)(matchLength - LengthBase[lengthCode]), lengthExtra);

                int distanceCode = DistanceCode(distance);
                Write(DistanceCodes[distanceCode], 5);
                int distanceExtra = DistanceExtraBits[distanceCode];
                if (distanceExtra > 0)
                    Write((uint)(distance - DistanceBase[distanceCode]), distanceExtra);
            }

            public void AlignToByte()
            {
                if (_bitCount > 0)
                {
                    _buffer[Position++] = (byte)_bits;
                    _bits = 0;
                    _bitCount = 0;
                }
            }
        }
    }
}
