using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// RFC 7541 Appendix B: the static Huffman code. The expected codes are transcribed from the RFC,
    /// not derived from <see cref="HpackHuffman"/>, so a wrong table entry cannot pass by round-tripping
    /// through itself.
    /// </summary>
    [TestFixture]
    public class HpackHuffmanAppendixBTests
    {
        // (code, bit length) for symbols 0-255 and EOS (256), as printed in RFC 7541 Appendix B.
        private static readonly (uint Code, int BitLength)[] s_appendixB =
        {
            (0x1ff8, 13), (0x7fffd8, 23), (0xfffffe2, 28), (0xfffffe3, 28), // 0-3
            (0xfffffe4, 28), (0xfffffe5, 28), (0xfffffe6, 28), (0xfffffe7, 28), // 4-7
            (0xfffffe8, 28), (0xffffea, 24), (0x3ffffffc, 30), (0xfffffe9, 28), // 8-11
            (0xfffffea, 28), (0x3ffffffd, 30), (0xfffffeb, 28), (0xfffffec, 28), // 12-15
            (0xfffffed, 28), (0xfffffee, 28), (0xfffffef, 28), (0xffffff0, 28), // 16-19
            (0xffffff1, 28), (0xffffff2, 28), (0x3ffffffe, 30), (0xffffff3, 28), // 20-23
            (0xffffff4, 28), (0xffffff5, 28), (0xffffff6, 28), (0xffffff7, 28), // 24-27
            (0xffffff8, 28), (0xffffff9, 28), (0xffffffa, 28), (0xffffffb, 28), // 28-31
            (0x14, 6), (0x3f8, 10), (0x3f9, 10), (0xffa, 12), // 32-35
            (0x1ff9, 13), (0x15, 6), (0xf8, 8), (0x7fa, 11), // 36-39
            (0x3fa, 10), (0x3fb, 10), (0xf9, 8), (0x7fb, 11), // 40-43
            (0xfa, 8), (0x16, 6), (0x17, 6), (0x18, 6), // 44-47
            (0x0, 5), (0x1, 5), (0x2, 5), (0x19, 6), // 48-51
            (0x1a, 6), (0x1b, 6), (0x1c, 6), (0x1d, 6), // 52-55
            (0x1e, 6), (0x1f, 6), (0x5c, 7), (0xfb, 8), // 56-59
            (0x7ffc, 15), (0x20, 6), (0xffb, 12), (0x3fc, 10), // 60-63
            (0x1ffa, 13), (0x21, 6), (0x5d, 7), (0x5e, 7), // 64-67
            (0x5f, 7), (0x60, 7), (0x61, 7), (0x62, 7), // 68-71
            (0x63, 7), (0x64, 7), (0x65, 7), (0x66, 7), // 72-75
            (0x67, 7), (0x68, 7), (0x69, 7), (0x6a, 7), // 76-79
            (0x6b, 7), (0x6c, 7), (0x6d, 7), (0x6e, 7), // 80-83
            (0x6f, 7), (0x70, 7), (0x71, 7), (0x72, 7), // 84-87
            (0xfc, 8), (0x73, 7), (0xfd, 8), (0x1ffb, 13), // 88-91
            (0x7fff0, 19), (0x1ffc, 13), (0x3ffc, 14), (0x22, 6), // 92-95
            (0x7ffd, 15), (0x3, 5), (0x23, 6), (0x4, 5), // 96-99
            (0x24, 6), (0x5, 5), (0x25, 6), (0x26, 6), // 100-103
            (0x27, 6), (0x6, 5), (0x74, 7), (0x75, 7), // 104-107
            (0x28, 6), (0x29, 6), (0x2a, 6), (0x7, 5), // 108-111
            (0x2b, 6), (0x76, 7), (0x2c, 6), (0x8, 5), // 112-115
            (0x9, 5), (0x2d, 6), (0x77, 7), (0x78, 7), // 116-119
            (0x79, 7), (0x7a, 7), (0x7b, 7), (0x7ffe, 15), // 120-123
            (0x7fc, 11), (0x3ffd, 14), (0x1ffd, 13), (0xffffffc, 28), // 124-127
            (0xfffe6, 20), (0x3fffd2, 22), (0xfffe7, 20), (0xfffe8, 20), // 128-131
            (0x3fffd3, 22), (0x3fffd4, 22), (0x3fffd5, 22), (0x7fffd9, 23), // 132-135
            (0x3fffd6, 22), (0x7fffda, 23), (0x7fffdb, 23), (0x7fffdc, 23), // 136-139
            (0x7fffdd, 23), (0x7fffde, 23), (0xffffeb, 24), (0x7fffdf, 23), // 140-143
            (0xffffec, 24), (0xffffed, 24), (0x3fffd7, 22), (0x7fffe0, 23), // 144-147
            (0xffffee, 24), (0x7fffe1, 23), (0x7fffe2, 23), (0x7fffe3, 23), // 148-151
            (0x7fffe4, 23), (0x1fffdc, 21), (0x3fffd8, 22), (0x7fffe5, 23), // 152-155
            (0x3fffd9, 22), (0x7fffe6, 23), (0x7fffe7, 23), (0xffffef, 24), // 156-159
            (0x3fffda, 22), (0x1fffdd, 21), (0xfffe9, 20), (0x3fffdb, 22), // 160-163
            (0x3fffdc, 22), (0x7fffe8, 23), (0x7fffe9, 23), (0x1fffde, 21), // 164-167
            (0x7fffea, 23), (0x3fffdd, 22), (0x3fffde, 22), (0xfffff0, 24), // 168-171
            (0x1fffdf, 21), (0x3fffdf, 22), (0x7fffeb, 23), (0x7fffec, 23), // 172-175
            (0x1fffe0, 21), (0x1fffe1, 21), (0x3fffe0, 22), (0x1fffe2, 21), // 176-179
            (0x7fffed, 23), (0x3fffe1, 22), (0x7fffee, 23), (0x7fffef, 23), // 180-183
            (0xfffea, 20), (0x3fffe2, 22), (0x3fffe3, 22), (0x3fffe4, 22), // 184-187
            (0x7ffff0, 23), (0x3fffe5, 22), (0x3fffe6, 22), (0x7ffff1, 23), // 188-191
            (0x3ffffe0, 26), (0x3ffffe1, 26), (0xfffeb, 20), (0x7fff1, 19), // 192-195
            (0x3fffe7, 22), (0x7ffff2, 23), (0x3fffe8, 22), (0x1ffffec, 25), // 196-199
            (0x3ffffe2, 26), (0x3ffffe3, 26), (0x3ffffe4, 26), (0x7ffffde, 27), // 200-203
            (0x7ffffdf, 27), (0x3ffffe5, 26), (0xfffff1, 24), (0x1ffffed, 25), // 204-207
            (0x7fff2, 19), (0x1fffe3, 21), (0x3ffffe6, 26), (0x7ffffe0, 27), // 208-211
            (0x7ffffe1, 27), (0x3ffffe7, 26), (0x7ffffe2, 27), (0xfffff2, 24), // 212-215
            (0x1fffe4, 21), (0x1fffe5, 21), (0x3ffffe8, 26), (0x3ffffe9, 26), // 216-219
            (0xffffffd, 28), (0x7ffffe3, 27), (0x7ffffe4, 27), (0x7ffffe5, 27), // 220-223
            (0xfffec, 20), (0xfffff3, 24), (0xfffed, 20), (0x1fffe6, 21), // 224-227
            (0x3fffe9, 22), (0x1fffe7, 21), (0x1fffe8, 21), (0x7ffff3, 23), // 228-231
            (0x3fffea, 22), (0x3fffeb, 22), (0x1ffffee, 25), (0x1ffffef, 25), // 232-235
            (0xfffff4, 24), (0xfffff5, 24), (0x3ffffea, 26), (0x7ffff4, 23), // 236-239
            (0x3ffffeb, 26), (0x7ffffe6, 27), (0x3ffffec, 26), (0x3ffffed, 26), // 240-243
            (0x7ffffe7, 27), (0x7ffffe8, 27), (0x7ffffe9, 27), (0x7ffffea, 27), // 244-247
            (0x7ffffeb, 27), (0xffffffe, 28), (0x7ffffec, 27), (0x7ffffed, 27), // 248-251
            (0x7ffffee, 27), (0x7ffffef, 27), (0x7fffff0, 27), (0x3ffffee, 26), // 252-255
            (0x3fffffff, 30), // 256-256
        };

        [Test]
        public void EveryCode_MatchesAppendixB()
        {
            Assert.AreEqual(257, s_appendixB.Length);
            for (int symbol = 0; symbol <= 256; symbol++)
            {
                var (code, bitLength) = HpackHuffman.GetCode(symbol);
                Assert.AreEqual(s_appendixB[symbol].BitLength, bitLength, "bit length of symbol {0}", symbol);
                Assert.AreEqual(s_appendixB[symbol].Code, code, "code of symbol {0}", symbol);
            }
        }

        [Test]
        public void Code_IsCompleteAndPrefixFree()
        {
            // Kraft sum: a complete prefix code with a 30-bit maximum length covers exactly 2^30 leaves.
            long kraft = 0;
            for (int symbol = 0; symbol <= 256; symbol++)
                kraft += 1L << (30 - HpackHuffman.GetCode(symbol).BitLength);
            Assert.AreEqual(1L << 30, kraft, "Kraft sum");

            for (int a = 0; a <= 256; a++)
            {
                var (codeA, lengthA) = HpackHuffman.GetCode(a);
                for (int b = 0; b <= 256; b++)
                {
                    if (a == b)
                        continue;
                    var (codeB, lengthB) = HpackHuffman.GetCode(b);
                    if (lengthA > lengthB)
                        continue;
                    Assert.AreNotEqual(codeA, codeB >> (lengthB - lengthA),
                        "code of symbol {0} is a prefix of the code of symbol {1}", a, b);
                }
            }
        }

        [Test]
        public void EverySymbol_EncodesAndDecodesAsAppendixB()
        {
            for (int symbol = 0; symbol < 256; symbol++)
            {
                var data = new[] { (byte)symbol };
                var expected = EncodeWithAppendixB(data);

                CollectionAssert.AreEqual(expected, HpackHuffman.Encode(data), "encoding of symbol {0}", symbol);
                CollectionAssert.AreEqual(data, HpackHuffman.Decode(expected, 0, expected.Length),
                    "decoding of symbol {0}", symbol);
            }
        }

        [Test]
        public void AllSymbolsInOneString_EncodeAndDecodeAsAppendixB()
        {
            var data = new byte[512];
            for (int i = 0; i < 256; i++)
            {
                data[i] = (byte)i;
                data[511 - i] = (byte)i;
            }

            var expected = EncodeWithAppendixB(data);

            CollectionAssert.AreEqual(expected, HpackHuffman.Encode(data));
            CollectionAssert.AreEqual(data, HpackHuffman.Decode(expected, 0, expected.Length));
        }

        [Test]
        public void Decoder_HeaderValueWithOpenBrace_DecodesConformantBytes()
        {
            // Literal header field without indexing, new name (RFC 7541 Section 6.2.2):
            // name "x" raw; value "{" Huffman: 0x7ffe in 15 bits plus one EOS padding bit = FF FD.
            var block = new byte[] { 0x00, 0x01, (byte)'x', 0x82, 0xFF, 0xFD };

            var headers = new HpackDecoder().Decode(block, 0, block.Length);

            CollectionAssert.AreEqual(new List<(string, string)> { ("x", "{") }, headers);
        }

        // Independent bit packer over the transcribed table; pads with EOS 1-bits (Section 5.2).
        private static byte[] EncodeWithAppendixB(byte[] data)
        {
            var output = new List<byte>();
            ulong buffer = 0;
            int bits = 0;
            foreach (var b in data)
            {
                var (code, length) = s_appendixB[b];
                buffer = (buffer << length) | code;
                bits += length;
                while (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)(buffer >> bits));
                }
            }

            if (bits > 0)
                output.Add((byte)((buffer << (8 - bits)) | ((1UL << (8 - bits)) - 1)));
            return output.ToArray();
        }
    }
}
