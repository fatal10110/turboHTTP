using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// #219 review: pins the member-end window of <see cref="InflateLayerStream"/> (the DEFLATE data ends inside the
    /// last chunks handed to the inflater) at every offset around the hand-chunk boundaries, and the dispose race of
    /// a header phase (a dispose while the source read is pending must not let a new inflater decode).
    /// </summary>
    public class InflateLayerStreamBoundaryTests
    {
        private const int SweepRadius = 10;
        private const int MaxChunkMultiple = 3;

        [TestCase(true)]
        [TestCase(false)]
        public void MemberEndSweep_StreamingSyncWithExactBuffers_Decodes(bool gzip)
        {
            foreach (var testCase in SweepCases(gzip))
            {
                using var layer = new InflateLayerStream(new MemoryStream(testCase.Wire), leaveOpen: false, gzip: gzip);
                foreach (var member in testCase.Members)
                {
                    var decoded = new byte[member.Length];
                    var filled = 0;
                    while (filled < decoded.Length)
                    {
                        var read = layer.Read(decoded, filled, decoded.Length - filled);
                        Assert.Greater(read, 0, testCase.Name + ": ended before the member's decoded length.");
                        filled += read;
                    }

                    Assert.IsTrue(decoded.AsSpan().SequenceEqual(member), testCase.Name + ": decoded bytes differ.");
                }

                Assert.AreEqual(0, layer.Read(new byte[16], 0, 16), testCase.Name + ": expected the end of the body.");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MemberEndSweep_StreamingAsyncWithExactBuffers_Decodes(bool gzip)
        {
            AssertAsync.Run(async () =>
            {
                foreach (var testCase in SweepCases(gzip))
                {
                    using var layer = new InflateLayerStream(
                        new MemoryStream(testCase.Wire),
                        leaveOpen: false,
                        gzip: gzip);
                    foreach (var member in testCase.Members)
                    {
                        var decoded = new byte[member.Length];
                        var filled = 0;
                        while (filled < decoded.Length)
                        {
                            var read = await layer.ReadAsync(decoded.AsMemory(filled), CancellationToken.None)
                                .ConfigureAwait(false);
                            Assert.Greater(read, 0, testCase.Name + ": ended before the member's decoded length.");
                            filled += read;
                        }

                        Assert.IsTrue(
                            decoded.AsSpan().SequenceEqual(member),
                            testCase.Name + ": decoded bytes differ.");
                    }

                    var end = await layer.ReadAsync(new byte[16].AsMemory(), CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.AreEqual(0, end, testCase.Name + ": expected the end of the body.");
                }
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MemberEndSweep_BufferedPath_Decodes(bool gzip)
        {
            AssertAsync.Run(async () =>
            {
                foreach (var testCase in SweepCases(gzip))
                {
                    var headers = new HttpHeaders();
                    headers.Set("Content-Encoding", gzip ? "gzip" : "deflate");
                    var transport = new MockTransport(HttpStatusCode.OK, headers, testCase.Wire);
                    var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/boundary"));

                    using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                    var expected = Concat(testCase.Members);
                    Assert.IsTrue(
                        response.Body.ToArray().AsSpan().SequenceEqual(expected),
                        testCase.Name + ": decoded bytes differ.");
                }
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DisposeWhileHeaderPhaseReadsSource_Sync_ThrowsObjectDisposed(bool gzip)
        {
            var source = new DisposingSource(new MemoryStream(CreateWire(gzip, CreatePayload(4096, 7))));
            using var layer = new InflateLayerStream(source, leaveOpen: true, gzip: gzip);
            source.Layer = layer;

            Assert.Throws<ObjectDisposedException>(() => layer.Read(new byte[1024], 0, 1024));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DisposeWhileHeaderPhaseReadsSource_Async_ThrowsObjectDisposed(bool gzip)
        {
            AssertAsync.Run(async () =>
            {
                var source = new DisposingSource(new MemoryStream(CreateWire(gzip, CreatePayload(4096, 7))));
                using var layer = new InflateLayerStream(source, leaveOpen: true, gzip: gzip);
                source.Layer = layer;

                Exception failure = null;
                try
                {
                    await layer.ReadAsync(new byte[1024].AsMemory(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsInstanceOf<ObjectDisposedException>(failure, "A disposed layer must not decode.");
            });
        }

        /// <summary>
        /// gzip: two members whose DEFLATE data each end at <c>k * 2048 + d</c> bytes after their header;
        /// zlib: one stream whose DEFLATE data ends there. Stored blocks make the DEFLATE length exact.
        /// </summary>
        private static IEnumerable<SweepCase> SweepCases(bool gzip)
        {
            for (var multiple = 1; multiple <= MaxChunkMultiple; multiple++)
            {
                for (var delta = -SweepRadius; delta <= SweepRadius; delta++)
                {
                    var deflateLength = multiple * InflateLayerStream.MaxHandBytes + delta;
                    var name = (gzip ? "gzip" : "zlib") + " DEFLATE length " + deflateLength;
                    if (gzip)
                    {
                        var first = PayloadForDeflateLength(deflateLength, seed: multiple * 100 + delta + 50);
                        var second = PayloadForDeflateLength(deflateLength, seed: multiple * 1000 + delta + 500);
                        yield return new SweepCase(
                            name,
                            Concat(GzipMember(first), GzipMember(second)),
                            new[] { first, second });
                    }
                    else
                    {
                        var payload = PayloadForDeflateLength(deflateLength, seed: multiple * 100 + delta + 50);
                        yield return new SweepCase(name, ZlibStream(payload), new[] { payload });
                    }
                }
            }
        }

        private static byte[] PayloadForDeflateLength(int deflateLength, int seed)
        {
            var overhead = RawStored(CreatePayload(deflateLength, seed)).Length - deflateLength;
            for (var adjust = 0; adjust <= 16; adjust++)
            {
                foreach (var length in new[] { deflateLength - overhead - adjust, deflateLength - overhead + adjust })
                {
                    if (length < 0)
                        continue;

                    var payload = CreatePayload(length, seed);
                    if (RawStored(payload).Length == deflateLength)
                        return payload;
                }
            }

            Assert.Fail("No payload compresses to exactly " + deflateLength + " DEFLATE bytes.");
            return null;
        }

        private static byte[] RawStored(byte[] payload)
        {
            using var stream = new MemoryStream();
            using (var deflate = new DeflateStream(stream, CompressionLevel.NoCompression, leaveOpen: true))
                deflate.Write(payload, 0, payload.Length);

            return stream.ToArray();
        }

        private static byte[] CreateWire(bool gzip, byte[] payload)
        {
            return gzip ? GzipMember(payload) : ZlibStream(payload);
        }

        private static byte[] GzipMember(byte[] payload)
        {
            // RFC 1952 section 2.3: ID1 ID2 CM=8 FLG=0 MTIME=0 XFL=0 OS=255, DEFLATE data, CRC32 and ISIZE (LE).
            var header = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF };
            var trailer = new byte[8];
            WriteLittleEndian(trailer, 0, Crc32(payload));
            WriteLittleEndian(trailer, 4, (uint)payload.Length);
            return Concat(header, RawStored(payload), trailer);
        }

        private static byte[] ZlibStream(byte[] payload)
        {
            // RFC 1950 section 2.2: CMF=0x78 FLG=0x01 (no dictionary, check bits), DEFLATE data, Adler-32 (BE).
            var adler = Adler32(payload);
            var trailer = new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler };
            return Concat(new byte[] { 0x78, 0x01 }, RawStored(payload), trailer);
        }

        private static byte[] CreatePayload(int length, int seed)
        {
            var bytes = new byte[length];
            var state = (uint)seed * 2654435761u + 1;
            for (var i = 0; i < bytes.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                bytes[i] = (byte)state;
            }

            return bytes;
        }

        private static uint Crc32(byte[] data)
        {
            var crc = uint.MaxValue;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return crc ^ uint.MaxValue;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1;
            uint b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static void WriteLittleEndian(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts)
                length += part.Length;

            var result = new byte[length];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private sealed class SweepCase
        {
            internal SweepCase(string name, byte[] wire, byte[][] members)
            {
                Name = name;
                Wire = wire;
                Members = members;
            }

            internal string Name { get; }

            internal byte[] Wire { get; }

            internal byte[][] Members { get; }
        }

        /// <summary>A source whose read disposes the layer above it before returning bytes (an abort mid-read).</summary>
        private sealed class DisposingSource : Stream
        {
            private readonly Stream _inner;

            internal DisposingSource(Stream inner)
            {
                _inner = inner;
            }

            internal InflateLayerStream Layer { get; set; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Layer?.Dispose();
                return _inner.Read(buffer, offset, count);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Layer?.Dispose();
                return new ValueTask<int>(_inner.Read(buffer.Span));
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
