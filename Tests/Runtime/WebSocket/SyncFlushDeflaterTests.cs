using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #270: the managed encoder permessage-deflate uses where DeflateStream.Flush is not a sync flush
    /// (Unity Mono). Its output must end at a sync flush with 00 00 FF FF removed (RFC 7692 section 7.2.1),
    /// never contain a final block, and inflate both alone and through one retained inflater (7.2.2).
    /// </summary>
    [TestFixture]
    public sealed class SyncFlushDeflaterTests
    {
        private static readonly byte[] Tail = { 0x00, 0x00, 0xFF, 0xFF };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(6)]
        [TestCase(9)]
        public void Compress_MessagesInflateWithOneRetainedStream(int level)
        {
            var messages = new List<byte[]>
            {
                Encoding.UTF8.GetBytes("first message first message first message"),
                Array.Empty<byte>(),
                Encoding.UTF8.GetBytes(new string('x', 3000)),
                RandomBytes(seed: 1, length: SyncFlushDeflater.MaxStoredBlockLength + 4500),
                RepeatAcrossWindow(),
                new byte[] { 0x42 },
                Encoding.UTF8.GetBytes("abcabcabcabcabcabcabcabcabcabcabcabc"),
            };

            var wire = new MemoryStream();
            var expected = new MemoryStream();
            foreach (byte[] message in messages)
            {
                using var compressed = SyncFlushDeflater.Compress(message, level);
                byte[] payload = compressed.Memory.ToArray();

                Assert.IsFalse(EndsWithTail(payload), "The 00 00 FF FF tail must be removed (RFC 7692 7.2.1 step 3).");
                Assert.LessOrEqual(payload.Length, SyncFlushDeflater.StoredLength(message.Length), "Never larger than stored blocks.");
                CollectionAssert.AreEqual(message, InflateAlone(payload), "Each message inflates with a fresh inflater too.");

                wire.Write(payload, 0, payload.Length);
                wire.Write(Tail, 0, Tail.Length);
                expected.Write(message, 0, message.Length);
            }

            CollectionAssert.AreEqual(expected.ToArray(), Inflate(wire.ToArray()));
        }

        [Test]
        public void Compress_EmptyMessage_IsTheSingleFlushByte()
        {
            // RFC 7692 section 7.2.3.6: an empty message compresses to the empty stored block header 0x00.
            using var compressed = SyncFlushDeflater.Compress(ReadOnlySpan<byte>.Empty, 6);
            CollectionAssert.AreEqual(new byte[] { 0x00 }, compressed.Memory.ToArray());
        }

        [Test]
        public void Compress_IncompressibleData_UsesStoredBlocksOfAtMost65535Bytes()
        {
            byte[] message = RandomBytes(seed: 7, length: (2 * SyncFlushDeflater.MaxStoredBlockLength) + 10);
            using var compressed = SyncFlushDeflater.Compress(message, 9);
            byte[] payload = compressed.Memory.ToArray();

            Assert.AreEqual(message.Length + (3 * 5) + 1, payload.Length);
            Assert.AreEqual(0x00, payload[0], "BFINAL=0, BTYPE=00.");
            Assert.AreEqual(0xFF, payload[1]);
            Assert.AreEqual(0xFF, payload[2]);
            Assert.AreEqual(0x00, payload[3]);
            Assert.AreEqual(0x00, payload[4]);
            CollectionAssert.AreEqual(message, InflateAlone(payload));
        }

        [Test]
        public void Compress_RepetitiveData_UsesFixedHuffmanBlockWithoutFinalBit()
        {
            byte[] message = Encoding.UTF8.GetBytes(new string('a', 10000));
            using var compressed = SyncFlushDeflater.Compress(message, 1);
            byte[] payload = compressed.Memory.ToArray();

            Assert.Less(payload.Length, 200);
            Assert.AreEqual(0b010, payload[0] & 0b111, "BFINAL=0, BTYPE=01 (fixed Huffman).");
            CollectionAssert.AreEqual(message, InflateAlone(payload));
        }

        [Test]
        public void TransformOutbound_SyncFlushDeflater_RoundTripsThroughTransformInbound()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);
            extension.ForceSyncFlushDeflater = true;
            try
            {
                Assert.IsTrue(extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse("permessage-deflate; server_no_context_takeover")));

                byte[] original = Encoding.UTF8.GetBytes(new string('q', 5000) + "end");
                using var compressed = extension.TransformOutbound(original, WebSocketOpcode.Binary, out byte rsvBits);
                Assert.IsNotNull(compressed);
                Assert.AreEqual(0x40, rsvBits);
                Assert.Less(compressed.Memory.Length, original.Length);

                using var restored = extension.TransformInbound(compressed.Memory, WebSocketOpcode.Binary, rsvBits);
                CollectionAssert.AreEqual(original, restored.Memory.ToArray());
            }
            finally
            {
                extension.Dispose();
            }
        }

        private static byte[] InflateAlone(byte[] payload)
        {
            var withTail = new byte[payload.Length + Tail.Length];
            Buffer.BlockCopy(payload, 0, withTail, 0, payload.Length);
            Buffer.BlockCopy(Tail, 0, withTail, payload.Length, Tail.Length);
            return Inflate(withTail);
        }

        private static byte[] Inflate(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] RandomBytes(int seed, int length)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        // A block repeated at distances just inside and just outside the 32768-byte window, between
        // compressible filler so the fixed-Huffman form (not stored blocks) is what gets written.
        private static byte[] RepeatAcrossWindow()
        {
            byte[] block = RandomBytes(seed: 3, length: 600);
            var data = new MemoryStream();
            data.Write(block, 0, block.Length);
            WriteWords(data, seed: 4, length: SyncFlushDeflater.MaxDistance - 900);
            data.Write(block, 0, block.Length);
            WriteWords(data, seed: 5, length: SyncFlushDeflater.MaxDistance + 100);
            data.Write(block, 0, block.Length);
            return data.ToArray();
        }

        private static void WriteWords(MemoryStream data, int seed, int length)
        {
            string[] words = { "frame ", "deflate ", "window ", "socket ", "message ", "context ", "flush " };
            var random = new Random(seed);
            var text = new StringBuilder(length + 16);
            while (text.Length < length)
                text.Append(words[random.Next(words.Length)]);
            byte[] bytes = Encoding.ASCII.GetBytes(text.ToString(0, length));
            data.Write(bytes, 0, bytes.Length);
        }

        private static bool EndsWithTail(byte[] payload)
        {
            if (payload.Length < Tail.Length)
                return false;
            for (int i = 0; i < Tail.Length; i++)
            {
                if (payload[payload.Length - Tail.Length + i] != Tail[i])
                    return false;
            }
            return true;
        }
    }
}
