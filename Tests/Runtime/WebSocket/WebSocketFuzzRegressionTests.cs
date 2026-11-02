using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// Fixed-seed mutation tests for the WebSocket frame reader, message assembler (RFC 6455
    /// Sections 5.2-5.5) and permessage-deflate inflate (RFC 7692) (#112). Failures other than
    /// WebSocketException, WebSocketProtocolException and IOException (truncated frame), and any
    /// frame or message past the configured bounds, are defects.
    /// </summary>
    [TestFixture]
    public class WebSocketFuzzRegressionTests
    {
        private const int Iterations = 3000;
        private const int MaxLength = 2048;
        private const int MaxFrameSize = 64 * 1024;
        private const int MaxMessageSize = 256 * 1024;

        private static readonly byte[][] FrameStreams =
        {
            SeededMutator.Hex("810568656c6c6f"),
            SeededMutator.Hex("010368656c8002" + "6c6f"),
            SeededMutator.Hex("01026865" + "8900" + "80036c6c6f"),
            SeededMutator.Hex("827f0000000000000004" + "61626364"),
            SeededMutator.Hex("880603e9" + "62796521"),
            SeededMutator.Hex("890568656c6c6f" + "8a00"),
            // RFC 7692 Section 7.2.3.1: "Hello" compressed, whole and fragmented (RSV1 on the first frame).
            SeededMutator.Hex("c107f248cdc9c90700"),
            SeededMutator.Hex("4103f248cd8004c9c90700"),
        };

        // [u16 BE length][compressed message] repeated: RFC 7692 Sections 7.2.3.1 and 7.2.3.3.
        private static readonly byte[][] CompressedMessages =
        {
            SeededMutator.Hex("0007f248cdc9c90700"),
            SeededMutator.Hex("000b000500faff48656c6c6f00" + "0007f248cdc9c90700"),
            SeededMutator.Hex("000100"),
        };

        [Test]
        public void FrameReaderAndAssembler_MutatedFrameStreams_FailOnlyWithWebSocketErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("ws-frames", 112, Iterations, MaxLength, FrameStreams, async data =>
            {
                using var stream = new MemoryStream(data, writable: false);
                var reader = new WebSocketFrameReader(MaxFrameSize, allowedRsvMask: 0x40,
                    rejectMaskedServerFrames: true, maxMessageSize: MaxMessageSize);
                var assembler = new MessageAssembler(MaxMessageSize, 64);
                using var deflate = NegotiatedDeflate();
                bool fragmented = false;
                try
                {
                    while (true)
                    {
                        var lease = await reader.ReadAsync(stream, fragmented, CancellationToken.None);
                        if (lease == null)
                            return;

                        var frame = lease.Frame;
                        Assert.LessOrEqual(frame.PayloadLength, MaxFrameSize);
                        if (frame.IsControlFrame)
                            Assert.IsTrue(frame.IsFinal && frame.PayloadLength <= 125, "invalid control frame accepted");
                        else
                            fragmented = !frame.IsFinal;

                        if (!assembler.TryAssemble(lease, out var message))
                            continue;

                        using (message)
                        {
                            Assert.LessOrEqual(message.Payload.Length, MaxMessageSize);
                            if (message.IsRsv1Set)
                            {
                                using var inflated = deflate.TransformInbound(message.Payload, message.Opcode, message.RsvBits);
                                Assert.IsTrue(inflated == null || inflated.Memory.Length <= MaxMessageSize, "inflated past the limit");
                            }
                        }
                    }
                }
                catch (WebSocketException)
                {
                }
                catch (WebSocketProtocolException)
                {
                }
                catch (IOException)
                {
                }
            }));
        }

        [Test]
        public void PerMessageDeflate_MutatedCompressedMessages_FailOnlyWithDecompressionErrors()
        {
            AssertAsync.Run(() => SeededMutator.RunAsync("ws-inflate", 112, Iterations, MaxLength, CompressedMessages, data =>
            {
                using var deflate = NegotiatedDeflate();
                int pos = 0;
                try
                {
                    while (pos < data.Length)
                    {
                        int length = data.Length - pos;
                        if (length >= 2)
                        {
                            length = Math.Min((data[pos] << 8) | data[pos + 1], length - 2);
                            pos += 2;
                        }

                        using var inflated = deflate.TransformInbound(
                            new ReadOnlyMemory<byte>(data, pos, length), WebSocketOpcode.Binary, 0x40);
                        Assert.IsTrue(inflated == null || inflated.Memory.Length <= MaxMessageSize, "inflated past the limit");
                        pos += length;
                    }
                }
                catch (WebSocketException)
                {
                }

                return Task.CompletedTask;
            }));
        }

        private static PerMessageDeflateExtension NegotiatedDeflate()
        {
            var deflate = new PerMessageDeflateExtension(null, MaxMessageSize);
            Assert.IsTrue(deflate.AcceptNegotiation(new WebSocketExtensionParameters(
                "permessage-deflate", new Dictionary<string, string> { ["server_no_context_takeover"] = null })));
            return deflate;
        }
    }
}
