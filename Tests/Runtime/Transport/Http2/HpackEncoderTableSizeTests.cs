using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// RFC 7541 Section 4.2: dynamic table size changes are applied at the start of the next header
    /// block, signalling the smallest size since the last block first, then the final size.
    /// </summary>
    [TestFixture]
    public class HpackEncoderTableSizeTests
    {
        private static readonly List<(string Name, string Value)> s_headers =
            new List<(string Name, string Value)>
            {
                (":method", "GET"),
                ("x-probe", "probe-value")
            };

        [Test]
        public void LoweredThenRaised_EmitsMinimumThenFinal_DecoderStaysInSync()
        {
            using var encoder = new HpackEncoder();
            var decoder = new HpackDecoder();
            Decode(decoder, encoder.Encode(s_headers).ToArray());

            encoder.SetMaxDynamicTableSize(0);
            encoder.SetMaxDynamicTableSize(4096);
            decoder.SetMaxDynamicTableSize(4096);

            var block = encoder.Encode(s_headers).ToArray();
            // 001 00000 (size 0), then 001 11111 + 4065 (size 4096).
            CollectionAssert.AreEqual(new byte[] { 0x20, 0x3F, 0xE1, 0x1F }, block.Take(4).ToArray());
            CollectionAssert.AreEqual(s_headers, Decode(decoder, block));

            // The table was emptied by the minimum: both sides re-learn the entry and stay in sync.
            CollectionAssert.AreEqual(s_headers, Decode(decoder, encoder.Encode(s_headers).ToArray()));
        }

        [Test]
        public void RaisedThenLowered_EmitsSingleFinalUpdate()
        {
            using var encoder = new HpackEncoder();

            encoder.SetMaxDynamicTableSize(8192);
            encoder.SetMaxDynamicTableSize(2048);

            var block = encoder.Encode(s_headers).ToArray();
            // 001 11111 + 2017 (size 2048), then the first header field (indexed :method GET).
            CollectionAssert.AreEqual(new byte[] { 0x3F, 0xE1, 0x0F, 0x82 }, block.Take(4).ToArray());
        }

        [Test]
        public void UnchangedSize_EmitsNoUpdate()
        {
            using var encoder = new HpackEncoder();

            encoder.SetMaxDynamicTableSize(4096);

            Assert.AreEqual(0x82, encoder.Encode(s_headers).Span[0]);
        }

        [Test]
        public void SizeUpdate_EmittedOnlyOnceAcrossBlocks()
        {
            using var encoder = new HpackEncoder();

            encoder.SetMaxDynamicTableSize(1024);
            Assert.AreEqual(0x3F, encoder.Encode(s_headers).Span[0]);
            Assert.AreEqual(0x82, encoder.Encode(s_headers).Span[0]);
        }

        [Test]
        public void SizeChangesFromAnotherThread_WhileEncoding_DecoderStaysInSync()
        {
            AssertAsync.Run(async () =>
            {
                using var encoder = new HpackEncoder();
                var decoder = new HpackDecoder();
                var headers = new List<(string Name, string Value)>
                {
                    (":method", "GET"),
                    ("x-a", "value-a"),
                    ("x-b", "value-b"),
                    ("x-c", "value-c")
                };

                int stop = 0;
                var resizer = Task.Run(() =>
                {
                    int i = 0;
                    while (Volatile.Read(ref stop) == 0)
                        encoder.SetMaxDynamicTableSize((i++ & 1) == 0 ? 0 : 4096);
                });

                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        headers[3] = ("x-c", "value-" + (i % 7));
                        var block = encoder.Encode(headers).ToArray();
                        CollectionAssert.AreEqual(headers, Decode(decoder, block), $"block {i}");
                    }
                }
                finally
                {
                    Volatile.Write(ref stop, 1);
                    await resizer;
                }
            });
        }

        private static List<(string Name, string Value)> Decode(HpackDecoder decoder, byte[] block)
        {
            return decoder.Decode(block, 0, block.Length);
        }
    }
}
