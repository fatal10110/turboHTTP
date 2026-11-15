using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// RFC 7541 Sections 4.2 and 7.3: the encoder's own dynamic table is bounded by a local cap, whatever
    /// SETTINGS_HEADER_TABLE_SIZE the peer advertises, and size updates carry the capped value.
    /// </summary>
    [TestFixture]
    public class HpackEncoderTableCapTests
    {
        private static readonly List<(string Name, string Value)> s_headers =
            new List<(string Name, string Value)>
            {
                (":method", "GET"),
                ("x-probe", "probe-value")
            };

        [Test]
        public void PeerAdvertisesIntMax_UpdateCarriesCap_RepeatEmitsNothing()
        {
            using var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            encoder.SetMaxDynamicTableSize(int.MaxValue);
            decoder.SetMaxDynamicTableSize(int.MaxValue);
            var block = encoder.Encode(s_headers).ToArray();

            // 001 11111 + 4065 (size 4096, the cap), then indexed :method GET.
            CollectionAssert.AreEqual(new byte[] { 0x3F, 0xE1, 0x1F, 0x82 }, block.Take(4).ToArray());
            Assert.AreEqual(HpackEncoder.DefaultLocalTableSizeCap, encoder.DynamicTableMaxSize);
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));

            // The same peer value again is no change: no update.
            encoder.SetMaxDynamicTableSize(int.MaxValue);
            block = encoder.Encode(s_headers).ToArray();
            CollectionAssert.AreEqual(new byte[] { 0x82, 0xBE }, block);
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));
        }

        [Test]
        public void PeerAdvertisesIntMax_ManyUniqueHeaders_TableMemoryStaysWithinCap()
        {
            using var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();
            encoder.SetMaxDynamicTableSize(int.MaxValue);
            decoder.SetMaxDynamicTableSize(int.MaxValue);
            string value = new string('v', 100);

            for (int i = 0; i < 1000; i++)
            {
                var headers = new List<(string Name, string Value)> { ("x-unique-" + i, value) };
                CollectionAssert.AreEqual(headers, Decode(decoder, encoder.Encode(headers).ToArray()));
                Assert.LessOrEqual(encoder.DynamicTableSize, HpackEncoder.DefaultLocalTableSizeCap);
            }
        }

        [Test]
        public void LoweredThenRaisedToIntMax_EmitsMinimumThenCap()
        {
            using var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            encoder.SetMaxDynamicTableSize(0);
            encoder.SetMaxDynamicTableSize(int.MaxValue);
            decoder.SetMaxDynamicTableSize(int.MaxValue);
            var block = encoder.Encode(s_headers).ToArray();

            // 001 00000 (size 0), then 001 11111 + 4065 (size 4096), then indexed :method GET.
            CollectionAssert.AreEqual(new byte[] { 0x20, 0x3F, 0xE1, 0x1F, 0x82 }, block.Take(5).ToArray());
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));
        }

        [Test]
        public void PeerBelowCap_IsHonoured()
        {
            using var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();

            encoder.SetMaxDynamicTableSize(256);
            decoder.SetMaxDynamicTableSize(256);
            var block = encoder.Encode(s_headers).ToArray();

            // 001 11111 + 225 (size 256).
            CollectionAssert.AreEqual(new byte[] { 0x3F, 0xE1, 0x01, 0x82 }, block.Take(4).ToArray());
            Assert.AreEqual(256, encoder.DynamicTableMaxSize);
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));

            encoder.SetMaxDynamicTableSize(0);
            block = encoder.Encode(s_headers).ToArray();
            Assert.AreEqual(0x20, block[0], "size 0 must be signalled");
            Assert.AreEqual(0, encoder.DynamicTableSize);
        }

        [Test]
        public void LocalCapBelowInitialSize_FirstBlockSignalsCap()
        {
            using var encoder = new HpackEncoder(4096, 1024);
            var decoder = new HpackDecoder();

            var block = encoder.Encode(s_headers).ToArray();
            // 001 11111 + 993 (size 1024).
            CollectionAssert.AreEqual(new byte[] { 0x3F, 0xE1, 0x07, 0x82 }, block.Take(4).ToArray());
            Assert.AreEqual(1024, encoder.DynamicTableMaxSize);
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));

            encoder.SetMaxDynamicTableSize(65536);
            decoder.SetMaxDynamicTableSize(65536);
            block = encoder.Encode(s_headers).ToArray();
            // The raised peer limit is answered with the cap again; the table keeps its entry.
            CollectionAssert.AreEqual(new byte[] { 0x3F, 0xE1, 0x07, 0x82, 0xBE }, block);
            Assert.AreEqual(1024, encoder.DynamicTableMaxSize);
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));
        }

        private static List<(string Name, string Value)> Decode(HpackDecoder decoder, byte[] block)
            => decoder.Decode(block, 0, block.Length);
    }
}
