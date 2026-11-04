using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #81: unoffered, duplicate or unhonourable server extensions always fail the handshake.
    /// #82: permessage-deflate offers and requires only what it can honour; control frames never carry RSV bits.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketExtensionStrictnessTests
    {
        private const string ValidDeflateResponse =
            "permessage-deflate; server_no_context_takeover; client_no_context_takeover";

        private static readonly Uri TestUri = new Uri("ws://localhost/extensions");

        [Test]
        public void Negotiator_NoConfiguredExtensions_RejectsAnyServerExtension()
        {
            var negotiator = new WebSocketExtensionNegotiator(Array.Empty<IWebSocketExtension>());

            Assert.IsFalse(negotiator.ProcessNegotiation("permessage-deflate").IsSuccess);
            Assert.IsTrue(negotiator.ProcessNegotiation(string.Empty).IsSuccess);
        }

        [Test]
        public void Negotiator_RejectsDuplicateExtension()
        {
            using var deflate = new PerMessageDeflateExtension();
            var negotiator = new WebSocketExtensionNegotiator(new IWebSocketExtension[] { deflate });

            var result = negotiator.ProcessNegotiation(ValidDeflateResponse + ", " + ValidDeflateResponse);

            Assert.IsFalse(result.IsSuccess);
        }

        [Test]
        public void Deflate_Offer_RequestsServerNoContextTakeover_AndNeverClientMaxWindowBits()
        {
            using var deflate = new PerMessageDeflateExtension();

            var offers = deflate.BuildOffers();

            Assert.AreEqual(1, offers.Count);
            Assert.IsTrue(offers[0].Parameters.ContainsKey("server_no_context_takeover"));
            Assert.IsFalse(offers[0].Parameters.ContainsKey("client_max_window_bits"));
        }

        [TestCase("permessage-deflate; server_no_context_takeover; client_max_window_bits=10")]
        [TestCase("permessage-deflate; server_no_context_takeover; client_max_window_bits=15")]
        [TestCase("permessage-deflate; client_no_context_takeover")]
        [TestCase("permessage-deflate")]
        [TestCase("permessage-deflate; server_no_context_takeover; x-unknown=1")]
        [TestCase("permessage-deflate; server_no_context_takeover; server_max_window_bits=7")]
        public void Deflate_RejectsResponseItCannotHonour(string response)
        {
            using var deflate = new PerMessageDeflateExtension();

            Assert.IsFalse(deflate.AcceptNegotiation(WebSocketExtensionParameters.Parse(response)));
        }

        [TestCase(ValidDeflateResponse)]
        [TestCase("permessage-deflate; server_no_context_takeover")]
        [TestCase("permessage-deflate; server_no_context_takeover; server_max_window_bits=10")]
        public void Deflate_AcceptsHonourableResponse(string response)
        {
            using var deflate = new PerMessageDeflateExtension();

            Assert.IsTrue(deflate.AcceptNegotiation(WebSocketExtensionParameters.Parse(response)));
        }

        [Test]
        public void DeflateOptions_RejectUnsupportedContextTakeoverAndClientWindow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PerMessageDeflateOptions(serverNoContextTakeover: false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PerMessageDeflateOptions(clientMaxWindowBits: 12));
            Assert.DoesNotThrow(() => new PerMessageDeflateOptions(serverMaxWindowBits: 10));
        }

        [Test]
        public void Options_Validate_RejectsRawExtensionStrings()
        {
            var options = new WebSocketConnectionOptions { Extensions = new[] { "x-custom" } };

            Assert.Throws<ArgumentException>(options.Validate);

            options.Extensions = new[] { " " };
            Assert.DoesNotThrow(options.Validate);
        }

        [TestCase(false, "permessage-deflate")]
        [TestCase(true, "x-unknown-extension")]
        [TestCase(true, "permessage-deflate; server_no_context_takeover; client_max_window_bits=10")]
        [TestCase(true, "permessage-deflate; client_no_context_takeover")]
        [TestCase(true, ValidDeflateResponse + ", " + ValidDeflateResponse)]
        public void Connect_ServerExtensionResponseRejected_FailsWithProtocolError(bool offerCompression, string response)
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer { HandshakeExtensionsHeader = response };
                var connection = new WebSocketConnection();
                try
                {
                    var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8);
                    if (offerCompression)
                        options.WithCompression();

                    var ex = AssertAsync.ThrowsAsync<WebSocketException>(async () =>
                        await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                            .ConfigureAwait(false));

                    Assert.AreEqual(WebSocketError.ExtensionNegotiationFailed, ex.Error);
                    Assert.AreEqual(WebSocketCloseCode.ProtocolError, ex.CloseCode);
                    Assert.AreEqual(WebSocketState.Closed, connection.State);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void Reader_ControlFrameWithRsv1_RejectedEvenWhenDeflateIsNegotiated()
        {
            using var stream = new MemoryStream(new byte[] { 0x80 | 0x40 | (byte)WebSocketOpcode.Ping, 0x00 });
            var reader = new WebSocketFrameReader(allowedRsvMask: 0x40);

            var ex = AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
                await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false));

            Assert.AreEqual(WebSocketError.ProtocolViolation, ex.Error);
            Assert.AreEqual(WebSocketCloseCode.ProtocolError, ex.CloseCode);
        }

        [Test]
        public void Connection_ControlFrameWithRsv1_AfterDeflateNegotiated_ClosesWith1002()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer { HandshakeExtensionsHeader = ValidDeflateResponse };
                var connection = new WebSocketConnection();
                try
                {
                    var options = WebSocketReceiveBackpressureTests.CreateOptions(capacity: 8).WithCompression();
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(1, connection.NegotiatedExtensions.Count);

                    await peer.SendRawAsync(new byte[] { 0x80 | 0x40 | (byte)WebSocketOpcode.Ping, 0x00 })
                        .ConfigureAwait(false);

                    var frame = await peer.ReadFrameAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.AreEqual(WebSocketOpcode.Close, frame.Opcode, "RSV1 on a ping was not rejected.");
                    Assert.AreEqual((int)WebSocketCloseCode.ProtocolError, (frame.Payload[0] << 8) | frame.Payload[1]);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }
    }
}
