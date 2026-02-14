using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    [TestFixture]
    public class Http2FrameCodecTests
    {
        [Test]
        public void DataFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = Http2FrameFlags.EndStream,
                    StreamId = 1,
                    Payload = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F },
                    Length = 5
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HeadersFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = 3,
                    Payload = new byte[] { 0x82, 0x86, 0x84 },
                    Length = 3
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SettingsFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                var payload = new byte[] { 0x00, 0x02, 0x00, 0x00, 0x00, 0x00 }; // ENABLE_PUSH=0
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = 6
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PingFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = payload,
                    Length = 8
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void GoAwayFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                var payload = new byte[8];
                payload[3] = 5; // Last-Stream-ID = 5
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = 8
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WindowUpdateFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                var payload = new byte[] { 0x00, 0x00, 0xFF, 0xFF };
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = 1,
                    Payload = payload,
                    Length = 4
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RstStreamFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                var payload = new byte[] { 0x00, 0x00, 0x00, 0x08 }; // CANCEL
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.RstStream,
                    Flags = Http2FrameFlags.None,
                    StreamId = 1,
                    Payload = payload,
                    Length = 4
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ContinuationFrame_RoundTrip()        {
            Task.Run(async () =>
            {
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = 1,
                    Payload = new byte[] { 0x01, 0x02 },
                    Length = 2
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamId_31BitMasking_HighBitCleared()        {
            Task.Run(async () =>
            {
                var ms = new MemoryStream();
                var codec = new Http2FrameCodec(ms);

                // Write frame with max stream ID (2^31 - 1)
                var frame = new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    StreamId = int.MaxValue,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                };
                await codec.WriteFrameAsync(frame, CancellationToken.None);

                ms.Position = 0;
                var read = await codec.ReadFrameAsync(Http2Constants.DefaultMaxFrameSize, CancellationToken.None);
                Assert.AreEqual(int.MaxValue, read.StreamId);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StreamId_Zero_ConnectionLevel()        {
            Task.Run(async () =>
            {
                await AssertFrameRoundTrip(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                });
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ZeroPayload_EmptyArray()        {
            Task.Run(async () =>
            {
                var ms = new MemoryStream();
                var codec = new Http2FrameCodec(ms);

                await codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);

                ms.Position = 0;
                var read = await codec.ReadFrameAsync(Http2Constants.DefaultMaxFrameSize, CancellationToken.None);
                Assert.AreEqual(0, read.Length);
                Assert.IsNotNull(read.Payload);
                Assert.AreEqual(0, read.Payload.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void FrameSizeValidation_RejectsOversized()        {
            Task.Run(async () =>
            {
                var ms = new MemoryStream();
                // Write a raw frame header with length > maxFrameSize
                var header = new byte[9];
                header[0] = 0x01; // length = 65536
                header[1] = 0x00;
                header[2] = 0x00;
                header[3] = 0x00; // type DATA
                // Write header + enough dummy payload
                ms.Write(header, 0, 9);
                ms.Write(new byte[65536], 0, 65536);
                ms.Position = 0;

                var codec = new Http2FrameCodec(ms);
                AssertAsync.ThrowsAsync<Http2ProtocolException>(
                    () => codec.ReadFrameAsync(Http2Constants.DefaultMaxFrameSize, CancellationToken.None));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void WritePrefaceAsync_Correct24Bytes()        {
            Task.Run(async () =>
            {
                var ms = new MemoryStream();
                var codec = new Http2FrameCodec(ms);
                await codec.WritePrefaceAsync(CancellationToken.None);

                var written = ms.ToArray();
                Assert.AreEqual(Http2Constants.ConnectionPreface.Length, written.Length);
                Assert.AreEqual(Http2Constants.ConnectionPreface, written);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HasFlag_EndStream()
        {
            var frame = new Http2Frame { Flags = Http2FrameFlags.EndStream };
            Assert.IsTrue(frame.HasFlag(Http2FrameFlags.EndStream));
            Assert.IsFalse(frame.HasFlag(Http2FrameFlags.EndHeaders));
        }

        [Test]
        public void HasFlag_Multiple()
        {
            var frame = new Http2Frame
            {
                Flags = Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders
            };
            Assert.IsTrue(frame.HasFlag(Http2FrameFlags.EndStream));
            Assert.IsTrue(frame.HasFlag(Http2FrameFlags.EndHeaders));
            Assert.IsFalse(frame.HasFlag(Http2FrameFlags.Padded));
        }

        private static async Task AssertFrameRoundTrip(Http2Frame original)
        {
            var ms = new MemoryStream();
            var codec = new Http2FrameCodec(ms);

            await codec.WriteFrameAsync(original, CancellationToken.None);
            ms.Position = 0;
            var read = await codec.ReadFrameAsync(Http2Constants.DefaultMaxFrameSize, CancellationToken.None);

            Assert.AreEqual(original.Type, read.Type);
            Assert.AreEqual(original.Flags, read.Flags);
            Assert.AreEqual(original.StreamId, read.StreamId);
            Assert.AreEqual(original.Payload.Length, read.Length);
            Assert.AreEqual(original.Payload, read.Payload);
        }
    }
}
