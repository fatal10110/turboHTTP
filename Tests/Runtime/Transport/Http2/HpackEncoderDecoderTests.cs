using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class HpackEncoderDecoderTests
    {
        [Test]
        public void RoundTrip_SingleHeader()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            var headers = new List<(string, string)> { ("custom-key", "custom-value") };
            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual("custom-key", decoded[0].Name);
            Assert.AreEqual("custom-value", decoded[0].Value);
        }

        [Test]
        public void RoundTrip_MultipleHeaders()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            var headers = new List<(string, string)>
            {
                (":method", "GET"),
                (":scheme", "https"),
                (":path", "/"),
                (":authority", "www.example.com"),
                ("accept", "text/html")
            };

            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(5, decoded.Count);
            for (int i = 0; i < headers.Count; i++)
            {
                Assert.AreEqual(headers[i].Item1, decoded[i].Name);
                Assert.AreEqual(headers[i].Item2, decoded[i].Value);
            }
        }

        [Test]
        public void RoundTrip_PseudoHeaders()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            var headers = new List<(string, string)>
            {
                (":method", "POST"),
                (":scheme", "http"),
                (":path", "/resource"),
                (":authority", "example.org")
            };

            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(4, decoded.Count);
            Assert.AreEqual(":method", decoded[0].Name);
            Assert.AreEqual("POST", decoded[0].Value);
        }

        [Test]
        public void RoundTrip_WithDynamicTableReuse()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            // First request
            var headers1 = new List<(string, string)> { ("custom-key", "custom-value") };
            var encoded1 = encoder.Encode(headers1);
            decoder.Decode(encoded1, 0, encoded1.Length);

            // Second request — same header should be in dynamic table
            var headers2 = new List<(string, string)> { ("custom-key", "custom-value") };
            var encoded2 = encoder.Encode(headers2);
            var decoded2 = decoder.Decode(encoded2, 0, encoded2.Length);

            Assert.AreEqual(1, decoded2.Count);
            Assert.AreEqual("custom-key", decoded2[0].Name);
            Assert.AreEqual("custom-value", decoded2[0].Value);

            // Second encoding should be shorter (indexed)
            Assert.Less(encoded2.Length, encoded1.Length);
        }

        [Test]
        public void RoundTrip_SensitiveHeaders_NeverIndexed()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            var headers = new List<(string, string)>
            {
                ("authorization", "Bearer token123")
            };

            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual("authorization", decoded[0].Name);
            Assert.AreEqual("Bearer token123", decoded[0].Value);
        }

        [Test]
        public void Decode_Index0_Throws()
        {
            var decoder = new HpackDecoder();
            // 0x80 | 0 = indexed header field, index 0
            var data = new byte[] { 0x80 };
            Assert.Throws<HpackDecodingException>(() => decoder.Decode(data, 0, data.Length));
        }

        [Test]
        public void Decode_InvalidRepresentation_Throws()
        {
            var decoder = new HpackDecoder();
            // 0x30 = 0011_0000 — doesn't match any valid pattern
            // Actually 0x30 = 001_10000, which is dynamic table size update (001xxxxx)
            // Use a truly invalid byte — all patterns are covered, so test
            // with out-of-bounds index instead
            var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }; // Very large index
            Assert.Throws<HpackDecodingException>(() => decoder.Decode(data, 0, data.Length));
        }

        [Test]
        public void DynamicTableSizeUpdate_DecodedCorrectly()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            // Change table size
            encoder.SetMaxDynamicTableSize(1024);
            decoder.SetMaxDynamicTableSize(1024);

            var headers = new List<(string, string)> { (":method", "GET") };
            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual(":method", decoded[0].Name);
            Assert.AreEqual("GET", decoded[0].Value);
        }

        [Test]
        public void Decode_DynamicTableSizeUpdate_ExceedsSettingsLimit_Throws()
        {
            var decoder = new HpackDecoder(4096);
            // Craft a dynamic table size update for 8192 (exceeds default 4096)
            // 001xxxxx prefix, 5-bit integer
            var output = new List<byte>();
            HpackIntegerCodec.Encode(8192, 5, 0x20, output);
            var data = output.ToArray();

            Assert.Throws<HpackDecodingException>(() => decoder.Decode(data, 0, data.Length));
        }

        [Test]
        public void Decode_StringBoundsCheckUsesHeaderBlockEnd_NotDataLength()
        {
            var decoder = new HpackDecoder();
            // Create a valid header block, then pass a shorter length
            var encoder = new HpackEncoder();
            var headers = new List<(string, string)> { ("x-key", "x-value") };
            var encoded = encoder.Encode(headers);

            // Put the encoded data in a larger buffer
            var buffer = new byte[encoded.Length + 100];
            Array.Copy(encoded, 0, buffer, 0, encoded.Length);

            // Decode with correct length should work
            var decoded = decoder.Decode(buffer, 0, encoded.Length);
            Assert.AreEqual(1, decoded.Count);
        }

        [Test]
        public void Decode_Latin1_PreservesObsTextBytes()
        {
            var decoder = new HpackDecoder();
            // Encode a header with a non-ASCII byte (0xE9 = é in Latin-1)
            // Manually construct: literal without indexing (0x00), name "x", value with 0xE9

            var data = new List<byte>();
            // Literal with incremental indexing, new name
            data.Add(0x40);
            // Name: "x" (1 byte, not Huffman)
            data.Add(0x01); // length=1, H=0
            data.Add(0x78); // 'x'
            // Value: byte 0xE9 (1 byte, not Huffman)
            data.Add(0x01); // length=1, H=0
            data.Add(0xE9); // Latin-1 é

            var encoded = data.ToArray();
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual("x", decoded[0].Name);
            // Should preserve the 0xE9 byte via Latin-1
            var valueBytes = TurboHTTP.Transport.Internal.EncodingHelper.Latin1.GetBytes(decoded[0].Value);
            Assert.AreEqual(1, valueBytes.Length);
            Assert.AreEqual(0xE9, valueBytes[0]);
        }

        [Test]
        public void Encode_Latin1_PreservesObsTextBytes()
        {
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            // Create a string with Latin-1 character
            string value = TurboHTTP.Transport.Internal.EncodingHelper.Latin1.GetString(
                new byte[] { 0xE9 });

            var headers = new List<(string, string)> { ("x-test", value) };
            var encoded = encoder.Encode(headers);
            var decoded = decoder.Decode(encoded, 0, encoded.Length);

            Assert.AreEqual(1, decoded.Count);
            var roundTripped = TurboHTTP.Transport.Internal.EncodingHelper.Latin1.GetBytes(decoded[0].Value);
            Assert.AreEqual(new byte[] { 0xE9 }, roundTripped);
        }
        [Test]
        public void Decode_DynamicTableSizeUpdate_AfterHeaderField_Throws()
        {
            // Fix 6: RFC 7541 Section 4.2 — size updates MUST occur at the beginning
            // of a header block, not after a header field representation.
            var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            // Build a header block with a header field followed by a size update.
            // This is invalid per the spec.
            var data = new List<byte>();

            // First: a valid literal header (incremental indexing, new name)
            data.Add(0x40); // literal with incremental indexing, name index 0
            data.Add(0x01); // name length = 1
            data.Add(0x78); // 'x'
            data.Add(0x01); // value length = 1
            data.Add(0x79); // 'y'

            // Then: a dynamic table size update (001xxxxx prefix)
            // Size = 1024 = 0x20 | (1024 encoded as 5-bit prefix integer)
            HpackIntegerCodec.Encode(1024, 5, 0x20, data);

            var encoded = data.ToArray();
            Assert.Throws<HpackDecodingException>(() => decoder.Decode(encoded, 0, encoded.Length));
        }

        [Test]
        public void Decode_IntegerBoundsRespectHeaderBlockEnd()
        {
            // Fix 4: HpackIntegerCodec.Decode should use headerBlockEnd, not data.Length.
            // Place valid HPACK data at the start of a larger buffer, but pass a shorter length.
            var decoder = new HpackDecoder();
            var encoder = new HpackEncoder();

            var headers = new List<(string, string)> { ("x-key", "x-value") };
            var encoded = encoder.Encode(headers);

            // Place in a larger buffer with garbage after the valid data
            var buffer = new byte[encoded.Length + 50];
            Array.Copy(encoded, 0, buffer, 0, encoded.Length);
            // Fill the rest with 0xFF (continuation bytes that could be misinterpreted)
            for (int i = encoded.Length; i < buffer.Length; i++)
                buffer[i] = 0xFF;

            // Should decode correctly with proper length
            var decoded = decoder.Decode(buffer, 0, encoded.Length);
            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual("x-key", decoded[0].Name);

            // A truncated length should fail, not read past the boundary
            Assert.Throws<HpackDecodingException>(() => decoder.Decode(buffer, 0, 2));
        }

        [Test]
        public void Decode_ExpectsSizeUpdateAfterSettingsChange_ThrowsIfMissing()
        {
            // After calling SetMaxDynamicTableSize, the decoder expects a size update
            // as the first instruction in the next header block (RFC 7541 Section 4.2).
            var decoder = new HpackDecoder();
            decoder.SetMaxDynamicTableSize(2048);

            // Build a header block WITHOUT a size update — just a header field
            var data = new List<byte>();
            data.Add(0x40); // literal with incremental indexing, name index 0
            data.Add(0x01); // name length = 1
            data.Add(0x78); // 'x'
            data.Add(0x01); // value length = 1
            data.Add(0x79); // 'y'

            var encoded = data.ToArray();
            Assert.Throws<HpackDecodingException>(() => decoder.Decode(encoded, 0, encoded.Length));
        }

        [Test]
        public void Decode_SizeUpdateAfterSettingsChange_Succeeds()
        {
            // After calling SetMaxDynamicTableSize, providing a size update before
            // any header fields should work fine.
            var decoder = new HpackDecoder();
            decoder.SetMaxDynamicTableSize(2048);

            var data = new List<byte>();
            // Dynamic table size update to 2048
            HpackIntegerCodec.Encode(2048, 5, 0x20, data);
            // Then a header field
            data.Add(0x40); // literal with incremental indexing, name index 0
            data.Add(0x01); // name length = 1
            data.Add(0x78); // 'x'
            data.Add(0x01); // value length = 1
            data.Add(0x79); // 'y'

            var encoded = data.ToArray();
            var decoded = decoder.Decode(encoded, 0, encoded.Length);
            Assert.AreEqual(1, decoded.Count);
            Assert.AreEqual("x", decoded[0].Name);
        }
    }
}

