using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1139 review: a permessage-deflate message that inflates past the limit keeps its specific
    /// <see cref="WebSocketError.DecompressedMessageTooLarge"/> and closes with 1009 (RFC 6455 section
    /// 7.4.1: a message too big to process), like every other size limit, not 1002.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketDecompressedLimitCloseTests
    {
        private const string DeflateResponse =
            "permessage-deflate; server_no_context_takeover; client_no_context_takeover";

        private static readonly Uri TestUri = new Uri("ws://localhost/deflate");
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void InflatedMessageOverLimit_ClosesWith1009_AndKeepsSpecificError()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer { HandshakeExtensionsHeader = DeflateResponse };
                var options = new WebSocketConnectionOptions
                {
                    HandshakeTimeout = Guard,
                    CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                    PingInterval = TimeSpan.Zero,
                    IdleTimeout = TimeSpan.Zero
                }.WithExtension(() => new PerMessageDeflateExtension(
                    new PerMessageDeflateOptions(compressionThreshold: 0),
                    maxMessageSize: 64));

                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, options, CancellationToken.None)
                        .ConfigureAwait(false);

                    await peer.SendRawAsync(BuildCompressedServerFrame(new string('x', 4096)))
                        .ConfigureAwait(false);

                    await WaitUntilAsync(() => connection.State == WebSocketState.Closed).ConfigureAwait(false);

                    Assert.AreEqual(WebSocketCloseCode.MessageTooBig, connection.CloseStatus?.Code);
                    var terminal = connection.TerminalError as WebSocketException;
                    Assert.IsNotNull(terminal, "TerminalError: " + connection.TerminalError);
                    Assert.AreEqual(WebSocketError.DecompressedMessageTooLarge, terminal.Error);
                    Assert.IsFalse(terminal.IsRetryable());
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        private static byte[] BuildCompressedServerFrame(string text)
        {
            var compressor = new PerMessageDeflateExtension(
                new PerMessageDeflateOptions(compressionThreshold: 0),
                maxMessageSize: 1024 * 1024);
            try
            {
                Assert.IsTrue(compressor.AcceptNegotiation(WebSocketExtensionParameters.Parse(DeflateResponse)));
                using var compressed = compressor.TransformOutbound(
                    Encoding.UTF8.GetBytes(text),
                    WebSocketOpcode.Binary,
                    out byte rsvBits);
                Assert.IsNotNull(compressed);

                // Unmasked server frame: FIN, the extension's RSV bits, opcode Binary (RFC 6455 section 5.2).
                var body = compressed.Memory.Span;
                Assert.Less(body.Length, 126, "The test frame uses the 7-bit payload length.");
                var frame = new byte[2 + body.Length];
                frame[0] = (byte)(0x80 | rsvBits | 0x02);
                frame[1] = (byte)body.Length;
                body.CopyTo(new Span<byte>(frame, 2, body.Length));
                return frame;
            }
            finally
            {
                compressor.Dispose();
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Guard;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("The condition was not met within the guard.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }
}
