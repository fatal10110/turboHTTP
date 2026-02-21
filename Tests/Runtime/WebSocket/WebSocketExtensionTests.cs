using System;
using System.Buffers;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketExtensionTests
    {
        [Test]
        public void ExtensionParameters_Parse_HandlesQuotedValuesAndFlags()
        {
            var parsed = WebSocketExtensionParameters.Parse(
                "permessage-deflate; x-token=abc; quoted=\"value\\\"x\"; server_no_context_takeover");

            Assert.AreEqual("permessage-deflate", parsed.ExtensionToken);
            Assert.AreEqual("abc", parsed.Parameters["x-token"]);
            Assert.AreEqual("value\"x", parsed.Parameters["quoted"]);
            Assert.IsTrue(parsed.Parameters.ContainsKey("server_no_context_takeover"));
            Assert.IsNull(parsed.Parameters["server_no_context_takeover"]);
        }

        [Test]
        public void Negotiator_RejectsUnsupportedServerExtension()
        {
            var negotiator = new WebSocketExtensionNegotiator(new[]
            {
                new TestExtension("permessage-deflate", 0x40)
            });

            var result = negotiator.ProcessNegotiation("x-custom");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("unsupported extension", result.ErrorMessage);
        }

        [Test]
        public void Negotiator_RejectsRsvOverlap()
        {
            var negotiator = new WebSocketExtensionNegotiator(new IWebSocketExtension[]
            {
                new TestExtension("ext-a", 0x40),
                new TestExtension("ext-b", 0x40)
            });

            var result = negotiator.ProcessNegotiation("ext-a, ext-b");

            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains("overlapping RSV", result.ErrorMessage);
        }

        [Test]
        public void PerMessageDeflate_RoundTripsPayload()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);

            try
            {
                bool accepted = extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));

                Assert.IsTrue(accepted);

                byte[] payload = Encoding.UTF8.GetBytes("hello hello hello hello hello hello hello hello");

                using var compressed = extension.TransformOutbound(
                    payload,
                    WebSocketOpcode.Text,
                    out byte rsvBits);

                Assert.IsNotNull(compressed);
                Assert.AreEqual(0x40, rsvBits);

                using var decompressed = extension.TransformInbound(
                    compressed.Memory,
                    WebSocketOpcode.Text,
                    rsvBits);

                Assert.IsNotNull(decompressed);
                CollectionAssert.AreEqual(payload, decompressed.Memory.ToArray());
            }
            finally
            {
                extension.Dispose();
            }
        }

        [Test]
        public void PerMessageDeflate_FragmentedCompressedMessage_Rsv1OnlyOnFirstFrame_AndRoundTrips()
        {
            AssertAsync.Run(async () =>
            {
                var extension = new PerMessageDeflateExtension(
                    new PerMessageDeflateOptions(compressionThreshold: 0),
                    maxMessageSize: 1024 * 1024);

                try
                {
                    bool accepted = extension.AcceptNegotiation(
                        WebSocketExtensionParameters.Parse(
                            "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));
                    Assert.IsTrue(accepted);

                    byte[] payload = Encoding.UTF8.GetBytes(new string('a', 2048));

                    using var transformed = extension.TransformOutbound(
                        payload,
                        WebSocketOpcode.Binary,
                        out byte rsvBits);
                    Assert.IsNotNull(transformed);
                    Assert.AreEqual(0x40, rsvBits);

                    using var stream = new MemoryStream();
                    using var writer = new WebSocketFrameWriter(fragmentationThreshold: 16);
                    var reader = new WebSocketFrameReader(allowedRsvMask: 0x40, rejectMaskedServerFrames: false);
                    var assembler = new MessageAssembler(maxMessageSize: 1024 * 1024, maxFragmentCount: 512);

                    await writer.WriteMessageAsync(
                        stream,
                        WebSocketOpcode.Binary,
                        transformed.Memory,
                        CancellationToken.None,
                        rsvBits).ConfigureAwait(false);

                    stream.Position = 0;

                    bool sawFirstFrame = false;
                    int continuationCount = 0;
                    WebSocketAssembledMessage assembled = null;
                    while (assembled == null)
                    {
                        using var lease = await reader.ReadAsync(
                            stream,
                            assembler.FragmentedMessageInProgress,
                            CancellationToken.None).ConfigureAwait(false);

                        Assert.IsNotNull(lease);
                        if (!sawFirstFrame)
                        {
                            sawFirstFrame = true;
                            Assert.IsTrue(lease.Frame.IsRsv1Set);
                        }
                        else
                        {
                            continuationCount++;
                            Assert.IsFalse(lease.Frame.IsRsv1Set);
                        }

                        if (assembler.TryAssemble(lease, out var maybeMessage))
                            assembled = maybeMessage;
                    }

                    Assert.Greater(continuationCount, 0);
                    using (assembled)
                    {
                        Assert.AreEqual(0x40, assembled.RsvBits);
                        using var decompressed = extension.TransformInbound(
                            assembled.Payload,
                            assembled.Opcode,
                            assembled.RsvBits);

                        Assert.IsNotNull(decompressed);
                        CollectionAssert.AreEqual(payload, decompressed.Memory.ToArray());
                    }
                }
                finally
                {
                    extension.Dispose();
                }
            });
        }

        [Test]
        public void ConnectionOptions_WithCompression_AddsExtensionFactory()
        {
            var options = new WebSocketConnectionOptions().WithCompression();

            Assert.AreEqual(1, options.ExtensionFactories.Count);

            var extension = options.ExtensionFactories[0]();
            try
            {
                Assert.IsNotNull(extension);
                Assert.AreEqual("permessage-deflate", extension.Name);
                Assert.AreEqual(0x40, extension.RsvBitMask);
            }
            finally
            {
                extension.Dispose();
            }
        }

        [Test]
        public void ConnectionOptions_WithRequiredCompression_EnablesRequiredNegotiation()
        {
            var options = new WebSocketConnectionOptions().WithRequiredCompression();

            Assert.IsTrue(options.RequireNegotiatedExtensions);
            Assert.AreEqual(1, options.ExtensionFactories.Count);
        }

        [Test]
        public void ConnectionOptions_Validate_ThrowsWhenRequiredNegotiationHasNoFactories()
        {
            var options = new WebSocketConnectionOptions
            {
                RequireNegotiatedExtensions = true
            };

            Assert.Throws<ArgumentException>(() => options.Validate());
        }

        [Test]
        public void PerMessageDeflate_BelowCompressionThreshold_Passthrough()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 256),
                maxMessageSize: 1024 * 1024);

            try
            {
                bool accepted = extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));
                Assert.IsTrue(accepted);

                byte[] payload = Encoding.UTF8.GetBytes("tiny");
                using var transformed = extension.TransformOutbound(
                    payload,
                    WebSocketOpcode.Text,
                    out byte rsvBits);

                Assert.IsNull(transformed);
                Assert.AreEqual(0, rsvBits);
            }
            finally
            {
                extension.Dispose();
            }
        }

        [Test]
        public void PerMessageDeflate_ControlFrames_AreNotCompressed()
        {
            var extension = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);

            try
            {
                bool accepted = extension.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));
                Assert.IsTrue(accepted);

                byte[] payload = Encoding.UTF8.GetBytes("ping");
                using var transformed = extension.TransformOutbound(
                    payload,
                    WebSocketOpcode.Ping,
                    out byte rsvBits);

                Assert.IsNull(transformed);
                Assert.AreEqual(0, rsvBits);
            }
            finally
            {
                extension.Dispose();
            }
        }

        [Test]
        public void PerMessageDeflate_DecompressionTooLarge_ThrowsSpecificError()
        {
            var compressor = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);
            var decompressor = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 64);

            try
            {
                bool acceptedCompressor = compressor.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));
                bool acceptedDecompressor = decompressor.AcceptNegotiation(
                    WebSocketExtensionParameters.Parse(
                        "permessage-deflate; server_no_context_takeover; client_no_context_takeover"));

                Assert.IsTrue(acceptedCompressor);
                Assert.IsTrue(acceptedDecompressor);

                byte[] payload = Encoding.UTF8.GetBytes(new string('x', 4096));

                using var compressed = compressor.TransformOutbound(
                    payload,
                    WebSocketOpcode.Binary,
                    out byte rsvBits);
                Assert.IsNotNull(compressed);

                var ex = Assert.Throws<WebSocketException>(() =>
                    _ = decompressor.TransformInbound(
                        compressed.Memory,
                        WebSocketOpcode.Binary,
                        rsvBits));

                Assert.AreEqual(WebSocketError.DecompressedMessageTooLarge, ex.Error);
            }
            finally
            {
                decompressor.Dispose();
                compressor.Dispose();
            }
        }

        private sealed class TestExtension : IWebSocketExtension
        {
            private readonly string _name;
            private readonly byte _rsvMask;

            public TestExtension(string name, byte rsvMask)
            {
                _name = name;
                _rsvMask = rsvMask;
            }

            public string Name => _name;

            public byte RsvBitMask => _rsvMask;

            public IReadOnlyList<WebSocketExtensionOffer> BuildOffers()
            {
                return new[] { new WebSocketExtensionOffer(_name) };
            }

            public bool AcceptNegotiation(WebSocketExtensionParameters serverParams)
            {
                return serverParams != null &&
                    string.Equals(serverParams.ExtensionToken, _name, StringComparison.OrdinalIgnoreCase);
            }

            public IMemoryOwner<byte> TransformOutbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, out byte rsvBits)
            {
                rsvBits = 0;
                return null;
            }

            public IMemoryOwner<byte> TransformInbound(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, byte rsvBits)
            {
                return null;
            }

            public void Reset()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
