using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.WebSocket
{
    [TestFixture]
    public sealed class WebSocketFramingTests
    {
        [Test]
        public void FrameRoundTrip_TextSmall_WriterToReader()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream();
                using var writer = new WebSocketFrameWriter(fragmentationThreshold: 1024);
                var reader = new WebSocketFrameReader(rejectMaskedServerFrames: false);

                await writer.WriteTextAsync(stream, "hello", CancellationToken.None).ConfigureAwait(false);
                stream.Position = 0;

                using var lease = await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.IsNotNull(lease);
                Assert.AreEqual(WebSocketOpcode.Text, lease.Frame.Opcode);
                Assert.IsTrue(lease.Frame.IsFinal);
                Assert.AreEqual("hello", Encoding.UTF8.GetString(lease.Frame.Payload.ToArray()));
            });
        }

        [Test]
        public void FrameRoundTrip_BinaryLarge_UsesExtendedLength()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream();
                using var writer = new WebSocketFrameWriter(fragmentationThreshold: int.MaxValue);
                var reader = new WebSocketFrameReader(rejectMaskedServerFrames: false);

                var payload = new byte[70_000];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i % 251);

                await writer.WriteBinaryAsync(stream, payload, CancellationToken.None).ConfigureAwait(false);
                stream.Position = 0;

                using var lease = await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.IsNotNull(lease);
                Assert.AreEqual(WebSocketOpcode.Binary, lease.Frame.Opcode);
                Assert.AreEqual(payload.Length, lease.Frame.Payload.Length);
                CollectionAssert.AreEqual(payload, lease.Frame.Payload.ToArray());
            });
        }

        [Test]
        public void Writer_FragmentsMessage_IntoContinuationFrames()
        {
            AssertAsync.Run(async () =>
            {
                using var stream = new MemoryStream();
                using var writer = new WebSocketFrameWriter(fragmentationThreshold: 4);
                var reader = new WebSocketFrameReader(rejectMaskedServerFrames: false);

                byte[] payload = Encoding.UTF8.GetBytes("fragment-me");
                await writer.WriteBinaryAsync(stream, payload, CancellationToken.None).ConfigureAwait(false);
                stream.Position = 0;

                using var first = await reader.ReadAsync(stream, fragmentedMessageInProgress: false, CancellationToken.None)
                    .ConfigureAwait(false);
                using var second = await reader.ReadAsync(stream, fragmentedMessageInProgress: true, CancellationToken.None)
                    .ConfigureAwait(false);
                using var third = await reader.ReadAsync(stream, fragmentedMessageInProgress: true, CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.AreEqual(WebSocketOpcode.Binary, first.Frame.Opcode);
                Assert.AreEqual(WebSocketOpcode.Continuation, second.Frame.Opcode);
                Assert.AreEqual(WebSocketOpcode.Continuation, third.Frame.Opcode);
                Assert.IsFalse(first.Frame.IsFinal);
                Assert.IsFalse(second.Frame.IsFinal);
                Assert.IsTrue(third.Frame.IsFinal);
            });
        }

        [Test]
        public void Reader_RejectsMaskedServerFrame_ByDefault()
        {
            using var stream = new MemoryStream(BuildFrame(
                opcode: WebSocketOpcode.Text,
                payload: Encoding.UTF8.GetBytes("masked"),
                masked: true,
                maskKey: 0x01020304u));

            var reader = new WebSocketFrameReader();

            var ex = AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
            {
                using var _ = await reader.ReadAsync(stream, false, CancellationToken.None).ConfigureAwait(false);
            });

            Assert.AreEqual(WebSocketError.MaskedServerFrame, ex.Error);
        }

        [Test]
        public void Reader_RejectsReservedOpcode()
        {
            var bytes = new byte[] { 0x83, 0x00 }; // FIN=1, opcode=0x3 reserved, len=0
            using var stream = new MemoryStream(bytes);

            var reader = new WebSocketFrameReader();

            var ex = AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
            {
                using var _ = await reader.ReadAsync(stream, false, CancellationToken.None).ConfigureAwait(false);
            });

            Assert.AreEqual(WebSocketError.ReservedOpcode, ex.Error);
        }

        [Test]
        public void WriteClose_RejectsReservedLocalCloseCodes()
        {
            using var stream = new MemoryStream();
            using var writer = new WebSocketFrameWriter();

            AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
                await writer.WriteCloseAsync(stream, WebSocketCloseCode.NoStatusReceived, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false));

            AssertAsync.ThrowsAsync<WebSocketProtocolException>(async () =>
                await writer.WriteCloseAsync(stream, WebSocketCloseCode.AbnormalClosure, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false));
        }

        private static byte[] BuildFrame(
            WebSocketOpcode opcode,
            byte[] payload,
            bool masked,
            uint maskKey)
        {
            payload ??= Array.Empty<byte>();
            int payloadLength = payload.Length;

            int headerLength = 2;
            if (payloadLength > 125 && payloadLength <= ushort.MaxValue)
                headerLength += 2;
            else if (payloadLength > ushort.MaxValue)
                headerLength += 8;
            if (masked)
                headerLength += 4;

            var result = new byte[headerLength + payloadLength];
            int offset = 0;
            result[offset++] = (byte)(0x80 | (byte)opcode);

            if (payloadLength <= 125)
            {
                result[offset++] = (byte)((masked ? 0x80 : 0x00) | payloadLength);
            }
            else if (payloadLength <= ushort.MaxValue)
            {
                result[offset++] = (byte)((masked ? 0x80 : 0x00) | 126);
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)payloadLength);
                offset += 2;
            }
            else
            {
                result[offset++] = (byte)((masked ? 0x80 : 0x00) | 127);
                BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(offset, 8), (ulong)payloadLength);
                offset += 8;
            }

            if (masked)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), maskKey);
                offset += 4;

                byte k0 = (byte)(maskKey >> 24);
                byte k1 = (byte)(maskKey >> 16);
                byte k2 = (byte)(maskKey >> 8);
                byte k3 = (byte)maskKey;

                for (int i = 0; i < payloadLength; i++)
                {
                    byte value = payload[i];
                    switch (i & 3)
                    {
                        case 0: value ^= k0; break;
                        case 1: value ^= k1; break;
                        case 2: value ^= k2; break;
                        default: value ^= k3; break;
                    }

                    result[offset + i] = value;
                }
            }
            else
            {
                Buffer.BlockCopy(payload, 0, result, offset, payloadLength);
            }

            return result;
        }
    }
}
