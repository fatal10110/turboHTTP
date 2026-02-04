using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class HpackIntegerCodecTests
    {
        // RFC 7541 Section C.1 test vectors

        [Test]
        public void Encode_10_Prefix5_SingleByte()
        {
            var output = new List<byte>();
            HpackIntegerCodec.Encode(10, 5, 0x00, output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(0x0A, output[0]);
        }

        [Test]
        public void Encode_1337_Prefix5_ThreeBytes()
        {
            var output = new List<byte>();
            HpackIntegerCodec.Encode(1337, 5, 0x00, output);
            Assert.AreEqual(3, output.Count);
            Assert.AreEqual(0x1F, output[0]);
            Assert.AreEqual(0x9A, output[1]);
            Assert.AreEqual(0x0A, output[2]);
        }

        [Test]
        public void Encode_42_Prefix8_SingleByte()
        {
            var output = new List<byte>();
            HpackIntegerCodec.Encode(42, 8, 0x00, output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(0x2A, output[0]);
        }

        [Test]
        public void RoundTrip_Zero()
        {
            AssertRoundTrip(0, 5);
        }

        [Test]
        public void RoundTrip_MaxPrefixMinusOne()
        {
            AssertRoundTrip(30, 5); // 2^5 - 1 - 1 = 30
        }

        [Test]
        public void RoundTrip_MaxPrefix()
        {
            AssertRoundTrip(31, 5); // 2^5 - 1 = 31, triggers multi-byte
        }

        [Test]
        public void RoundTrip_LargeValue_65535()
        {
            AssertRoundTrip(65535, 5);
        }

        [Test]
        public void RoundTrip_AllPrefixWidths_1Through8()
        {
            for (int prefix = 1; prefix <= 8; prefix++)
            {
                AssertRoundTrip(0, prefix);
                AssertRoundTrip(1, prefix);
                AssertRoundTrip(127, prefix);
                AssertRoundTrip(256, prefix);
                AssertRoundTrip(1337, prefix);
            }
        }

        [Test]
        public void Encode_31_Prefix5_TwoBytes()
        {
            var output = new List<byte>();
            HpackIntegerCodec.Encode(31, 5, 0x00, output);
            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(0x1F, output[0]);
            Assert.AreEqual(0x00, output[1]);
        }

        [Test]
        public void Decode_OverflowDetection_Throws()
        {
            // 5 continuation bytes (m would reach 35 > 28)
            var data = new byte[] { 0x1F, 0x80, 0x80, 0x80, 0x80, 0x80 };
            int offset = 0;
            Assert.Throws<HpackDecodingException>(() => HpackIntegerCodec.Decode(data, ref offset, 5));
        }

        [Test]
        public void Decode_UnexpectedEnd_Throws()
        {
            // Continuation bit set but no more data
            var data = new byte[] { 0x1F, 0x80 };
            int offset = 0;
            Assert.Throws<HpackDecodingException>(() => HpackIntegerCodec.Decode(data, ref offset, 5));
        }

        [Test]
        public void Decode_AdvancesOffset_SingleByte()
        {
            var data = new byte[] { 0x0A };
            int offset = 0;
            HpackIntegerCodec.Decode(data, ref offset, 5);
            Assert.AreEqual(1, offset);
        }

        [Test]
        public void Decode_AdvancesOffset_MultiByte()
        {
            var data = new byte[] { 0x1F, 0x9A, 0x0A };
            int offset = 0;
            int value = HpackIntegerCodec.Decode(data, ref offset, 5);
            Assert.AreEqual(1337, value);
            Assert.AreEqual(3, offset);
        }

        [Test]
        public void Encode_PreservesUpperBits()
        {
            var output = new List<byte>();
            // 0x80 prefix byte with 7-bit prefix, value 2
            HpackIntegerCodec.Encode(2, 7, 0x80, output);
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(0x82, output[0]); // 1000_0010
        }

        private static void AssertRoundTrip(int value, int prefixBits)
        {
            var output = new List<byte>();
            HpackIntegerCodec.Encode(value, prefixBits, 0x00, output);

            int offset = 0;
            int decoded = HpackIntegerCodec.Decode(output.ToArray(), ref offset, prefixBits);
            Assert.AreEqual(value, decoded, $"Round-trip failed for value={value}, prefix={prefixBits}");
            Assert.AreEqual(output.Count, offset);
        }
    }
}
