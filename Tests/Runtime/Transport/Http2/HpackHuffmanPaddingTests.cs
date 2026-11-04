using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// RFC 7541 Section 5.2: Huffman padding must be a strict prefix of the EOS code (all 1-bits)
    /// shorter than 8 bits; anything else is a decoding error (COMPRESSION_ERROR on the connection).
    /// </summary>
    [TestFixture]
    public class HpackHuffmanPaddingTests
    {
        [Test]
        public void Decode_PaddingOfOneBitsShorterThanEight_IsAccepted()
        {
            // 'a' = 00011 (5 bits) + 111 padding.
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("a"), HpackHuffman.Decode(new byte[] { 0x1F }, 0, 1));

            // RFC 7541 Appendix C.4.1: "www.example.com".
            var vector = new byte[] { 0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff };
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("www.example.com"),
                HpackHuffman.Decode(vector, 0, vector.Length));
        }

        [TestCase(new byte[] { 0x00 }, TestName = "Decode_PaddingAllZeroBits_Throws")]          // '0' + 000
        [TestCase(new byte[] { 0x1D }, TestName = "Decode_PaddingWithZeroInside_Throws")]       // 'a' + 101
        [TestCase(new byte[] { 0x1E }, TestName = "Decode_PaddingEndingInZero_Throws")]         // 'a' + 110
        public void Decode_PaddingThatIsNotAnEosPrefix_Throws(byte[] encoded)
        {
            Assert.Throws<HpackDecodingException>(() => HpackHuffman.Decode(encoded, 0, encoded.Length));
        }

        [Test]
        public void Decode_PaddingOfEightOrMoreBits_Throws()
        {
            // '&' = 11111000 (8 bits), then a whole byte of 1-bits as padding.
            Assert.Throws<HpackDecodingException>(() => HpackHuffman.Decode(new byte[] { 0xF8, 0xFF }, 0, 2));
        }

        [Test]
        public void Decode_EosSymbolInString_Throws()
        {
            // EOS (30 one-bits) followed by 2 one-bits.
            Assert.Throws<HpackDecodingException>(
                () => HpackHuffman.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4));
        }

        [Test]
        public void HpackDecoder_HuffmanStringWithInvalidPadding_ThrowsDecodingException()
        {
            // Literal without indexing, new name: name = Huffman "0" with 000 padding, value = "x".
            var block = new byte[] { 0x00, 0x81, 0x00, 0x01, (byte)'x' };
            var decoder = new HpackDecoder();
            Assert.Throws<HpackDecodingException>(
                () => decoder.Decode(block, 0, block.Length, new List<(string Name, string Value)>()));
        }
    }
}
