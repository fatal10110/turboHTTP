using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #217 (Autobahn 12.x, 13.1.x): when the server does not agree to client_no_context_takeover it may
    /// inflate every client message with one retained stream (RFC 7692 section 7.2.2). A compressed message
    /// must therefore end at a sync flush with its 00 00 FF FF removed (section 7.2.1), never with a final
    /// DEFLATE block after which such a stream stops.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketDeflateContextTakeoverTests
    {
        private static readonly byte[] Tail = { 0x00, 0x00, 0xFF, 0xFF };

        [Test]
        public void TransformOutbound_WithoutClientNoContextTakeover_InflatesWithOneRetainedServerStream()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);
            try
            {
                // The response Autobahn 12.x/13.1.x sends: no client_no_context_takeover.
                Assert.IsTrue(extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse("permessage-deflate; server_no_context_takeover")));

                string[] messages =
                {
                    "first message first message first message",
                    new string('x', 3000),
                    "third",
                };

                // What a context-takeover server does: append the tail to each compressed payload and feed
                // all of them to one inflater. A message sent uncompressed (RSV1 unset, where the runtime's
                // DeflateStream cannot sync-flush) is read as is and never reaches the inflater.
                using var wire = new MemoryStream();
                var expected = new StringBuilder();
                foreach (string text in messages)
                {
                    byte[] plain = Encoding.UTF8.GetBytes(text);
                    using var compressed = extension.TransformOutbound(plain, WebSocketOpcode.Text, out byte rsvBits);
                    if (compressed == null)
                    {
                        Assert.AreEqual(0, rsvBits);
                        continue;
                    }

                    Assert.AreEqual(0x40, rsvBits);
                    byte[] payload = compressed.Memory.ToArray();
                    Assert.IsFalse(EndsWithTail(payload), "The 00 00 FF FF tail must be removed (RFC 7692 7.2.1 step 3).");
                    wire.Write(payload, 0, payload.Length);
                    wire.Write(Tail, 0, Tail.Length);
                    expected.Append(text);
                }

                wire.Position = 0;
                using var inflate = new DeflateStream(wire, CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                inflate.CopyTo(inflated);

                Assert.AreEqual(expected.ToString(), Encoding.UTF8.GetString(inflated.ToArray()));
            }
            finally
            {
                extension.Dispose();
            }
        }

        [Test]
        public void TransformOutbound_WithClientNoContextTakeover_CompressesEveryMessageAndRoundTrips()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);
            try
            {
                Assert.IsTrue(extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover")));

                for (int i = 0; i < 3; i++)
                {
                    byte[] original = Encoding.UTF8.GetBytes(new string((char)('a' + i), 2048) + "end" + i);
                    using var compressed = extension.TransformOutbound(original, WebSocketOpcode.Binary, out byte rsvBits);
                    Assert.IsNotNull(compressed, "A server that resets its inflater per message accepts every compressed form.");
                    Assert.AreEqual(0x40, rsvBits);

                    using var restored = extension.TransformInbound(compressed.Memory, WebSocketOpcode.Binary, rsvBits);
                    CollectionAssert.AreEqual(original, restored.Memory.ToArray());
                }
            }
            finally
            {
                extension.Dispose();
            }
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
