using System;
using System.Collections.Generic;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK prefix-coded integer encoding/decoding. RFC 7541 Section 5.1.
    /// </summary>
    internal static class HpackIntegerCodec
    {
        /// <summary>
        /// Encode an integer with the given prefix bit width.
        /// The first byte's upper bits (above the prefix) are preserved from prefixByte.
        /// </summary>
        public static void Encode(int value, int prefixBits, byte prefixByte, List<byte> output)
        {
            int maxPrefix = (1 << prefixBits) - 1;

            if (value < maxPrefix)
            {
                output.Add((byte)(prefixByte | value));
            }
            else
            {
                output.Add((byte)(prefixByte | maxPrefix));
                value -= maxPrefix;
                while (value >= 128)
                {
                    output.Add((byte)((value & 0x7F) | 0x80));
                    value >>= 7;
                }
                output.Add((byte)value);
            }
        }

        /// <summary>
        /// Decode an integer with the given prefix bit width.
        /// The offset is advanced past the decoded integer.
        /// Bounds checking uses <paramref name="end"/> (not data.Length) to prevent
        /// reading past the header block boundary when data is in a larger buffer.
        /// </summary>
        public static int Decode(byte[] data, ref int offset, int prefixBits, int end)
        {
            if (offset >= end)
                throw new HpackDecodingException("Unexpected end of HPACK integer");

            int maxPrefix = (1 << prefixBits) - 1;
            int value = data[offset] & maxPrefix;
            offset++;

            if (value < maxPrefix)
                return value;

            int m = 0;
            byte b;
            do
            {
                if (offset >= end)
                    throw new HpackDecodingException("Unexpected end of HPACK integer");
                b = data[offset];
                offset++;
                value += (b & 0x7F) << m;
                m += 7;

                if (m > 28)
                    throw new HpackDecodingException("HPACK integer overflow");
            }
            while ((b & 0x80) != 0);

            return value;
        }

        /// <summary>
        /// Convenience overload that uses data.Length as the end bound.
        /// Only safe when the entire buffer is the header block (e.g., standalone encoding tests).
        /// </summary>
        public static int Decode(byte[] data, ref int offset, int prefixBits)
        {
            return Decode(data, ref offset, prefixBits, data.Length);
        }
    }
}
