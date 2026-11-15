using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1144: RFC 6455 section 5.4 sets no fragment limit and MaxMessageSize already bounds the bytes, so
    /// the default MaxFragmentCount admits real messages sent in 8 KiB frames (a 600 KB message, a
    /// default-size 4 MiB one in 512 fragments) and only stops a message that never ends in empty fragments.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketFragmentCountTests
    {
        private const int FragmentSize = 8 * 1024;
        private static readonly Uri TestUri = new Uri("ws://localhost/fragments");
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

        [Test]
        public void DefaultOptions_600KbMessageIn8KibFragments_Delivered()
        {
            AssertReceivesFragmentedMessage(600 * 1024);
        }

        [Test]
        public void DefaultOptions_DefaultSizeMessageIn8KibFragments_Delivered()
        {
            Assert.AreEqual(512, WebSocketConstants.DefaultMaxMessageSize / FragmentSize);
            AssertReceivesFragmentedMessage(WebSocketConstants.DefaultMaxMessageSize);
        }

        [Test]
        public void DefaultOptions_EmptyFragmentFlood_FailsWith1009()
        {
            AssertAsync.Run(async () =>
            {
                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    // An unfinished binary message followed by empty continuations, one more frame than the
                    // default limit: 2-byte unmasked server frames, so the flood is a few KB of wire bytes.
                    int frames = WebSocketConstants.DefaultMaxFragmentCount + 1;
                    var flood = new byte[frames * 2];
                    flood[0] = 0x02; // FIN=0, opcode Binary, length 0
                    for (int i = 1; i < frames; i++)
                        flood[i * 2] = 0x00; // FIN=0, opcode Continuation, length 0
                    await peer.SendRawAsync(flood).ConfigureAwait(false);

                    await WaitUntilAsync(() => connection.State == WebSocketState.Closed).ConfigureAwait(false);

                    Assert.AreEqual(WebSocketCloseCode.MessageTooBig, connection.CloseStatus?.Code);
                    Assert.IsInstanceOf<WebSocketException>(connection.TerminalError);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        [Test]
        public void DefaultOptions_FragmentLimitStillCoversMaxMessageSize()
        {
            var options = new WebSocketConnectionOptions();

            Assert.AreEqual(WebSocketConstants.DefaultMaxFragmentCount, options.MaxFragmentCount);
            Assert.GreaterOrEqual(options.MaxFragmentCount, options.MaxMessageSize / FragmentSize);
            Assert.DoesNotThrow(options.Validate);
        }

        private static void AssertReceivesFragmentedMessage(int messageSize)
        {
            AssertAsync.Run(async () =>
            {
                var payload = new byte[messageSize];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i * 31 + 7);

                using var peer = new InMemoryWebSocketPeer();
                var connection = new WebSocketConnection();
                try
                {
                    await connection.ConnectAsync(TestUri, peer, CreateOptions(), CancellationToken.None)
                        .ConfigureAwait(false);

                    using (var frames = new MemoryStream())
                    {
                        for (int offset = 0; offset < payload.Length; offset += FragmentSize)
                        {
                            int length = Math.Min(FragmentSize, payload.Length - offset);
                            AppendServerFrame(
                                frames,
                                offset == 0 ? (byte)0x02 : (byte)0x00,
                                offset + length == payload.Length,
                                payload,
                                offset,
                                length);
                        }

                        await peer.SendRawAsync(frames.ToArray()).ConfigureAwait(false);
                    }

                    using var cts = new CancellationTokenSource(Guard);
                    using var message = await connection.ReceiveAsync(cts.Token).ConfigureAwait(false);

                    Assert.AreEqual(payload.Length, message.Length);
                    AssertPayloadEquals(payload, message.Data);
                    Assert.AreEqual(WebSocketState.Open, connection.State);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        private static void AssertPayloadEquals(byte[] expected, ReadOnlyMemory<byte> actual)
        {
            var received = actual.Span;
            for (int i = 0; i < expected.Length; i++)
            {
                if (received[i] != expected[i])
                    Assert.Fail("The reassembled payload differs at byte " + i + ".");
            }
        }

        private static void AppendServerFrame(
            MemoryStream stream,
            byte opcode,
            bool fin,
            byte[] payload,
            int offset,
            int length)
        {
            stream.WriteByte((byte)((fin ? 0x80 : 0x00) | opcode));
            if (length < 126)
            {
                stream.WriteByte((byte)length);
            }
            else
            {
                // Fragments here are at most 8 KiB: the 16-bit extended length (RFC 6455 section 5.2).
                stream.WriteByte(126);
                stream.WriteByte((byte)(length >> 8));
                stream.WriteByte((byte)length);
            }

            stream.Write(payload, offset, length);
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

        private static WebSocketConnectionOptions CreateOptions()
        {
            return new WebSocketConnectionOptions
            {
                HandshakeTimeout = Guard,
                CloseHandshakeTimeout = TimeSpan.FromMilliseconds(500),
                PingInterval = TimeSpan.Zero,
                IdleTimeout = TimeSpan.Zero
            };
        }
    }
}
