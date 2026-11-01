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
    /// #219: <c>deflate</c> is the zlib format (RFC 9110 section 8.4.1.2, RFC 1950) with a raw DEFLATE fallback,
    /// and a gzip body may hold several members (RFC 1952 section 2.2), each with its own checked trailer.
    /// Every case runs on the buffered and the streaming decompression path.
    /// </summary>
    public class DecompressionZlibAndMultiMemberTests
    {
        private static readonly byte[] s_payload = CreatePayload(48 * 1024);

        [TestCase(true)]
        [TestCase(false)]
        public void ZlibDeflate_Decodes(bool buffered)
        {
            AssertDecodes("deflate", ZlibCompress(s_payload), s_payload, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RawDeflate_StillDecodes(bool buffered)
        {
            AssertDecodes("deflate", RawDeflateCompress(s_payload), s_payload, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ZlibDeflate_WithWrongAdler32_Fails(bool buffered)
        {
            var wire = ZlibCompress(s_payload);
            wire[wire.Length - 1] ^= 0x01;
            AssertFails("deflate", wire, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ZlibDeflate_TruncatedInsideAdler32_Fails(bool buffered)
        {
            var wire = ZlibCompress(s_payload);
            AssertFails("deflate", Cut(wire, wire.Length - 2), buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ZlibDeflate_WithTrailingData_Fails(bool buffered)
        {
            AssertFails("deflate", Concat(ZlibCompress(s_payload), new byte[] { 0x00 }), buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ZlibDeflate_WithPresetDictionary_Fails(bool buffered)
        {
            // CMF 0x78, FLG 0xBB: FDICT set and (CMF * 256 + FLG) % 31 == 0 (RFC 1950 section 2.2).
            var raw = RawDeflateCompress(s_payload);
            var wire = Concat(new byte[] { 0x78, 0xBB, 0, 0, 0, 1 }, raw, BigEndian(Adler32(s_payload)));
            Assert.AreEqual(0, (0x78 * 256 + 0xBB) % 31);
            AssertFails("deflate", wire, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TwoMemberGzip_DecodesBothMembers(bool buffered)
        {
            var first = Slice(s_payload, 0, 20000);
            var second = Slice(s_payload, 20000, s_payload.Length - 20000);
            AssertDecodes("gzip", Concat(GzipCompress(first), GzipCompress(second)), s_payload, buffered);
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        public void TwoMemberGzip_CorruptCrcOfEitherMember_Fails(bool buffered, int corruptMember)
        {
            var first = GzipCompress(Slice(s_payload, 0, 20000));
            var second = GzipCompress(Slice(s_payload, 20000, s_payload.Length - 20000));
            var wire = Concat(first, second);
            var trailerStart = corruptMember == 0 ? first.Length - 8 : wire.Length - 8;
            wire[trailerStart] ^= 0x01;
            AssertFails("gzip", wire, buffered);
        }

        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        public void TwoMemberGzip_WrongIsizeOfEitherMember_Fails(bool buffered, int corruptMember)
        {
            var first = GzipCompress(Slice(s_payload, 0, 20000));
            var second = GzipCompress(Slice(s_payload, 20000, s_payload.Length - 20000));
            var wire = Concat(first, second);
            var isizeStart = corruptMember == 0 ? first.Length - 4 : wire.Length - 4;
            wire[isizeStart] ^= 0x01;
            AssertFails("gzip", wire, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ManyMemberGzip_WithEmptyMembers_Decodes(bool buffered)
        {
            // Empty members have an all-zero trailer right after two DEFLATE bytes: the member end search must
            // not stop early inside it.
            var parts = new List<byte[]>
            {
                GzipCompress(Array.Empty<byte>()),
                GzipCompress(Slice(s_payload, 0, 7)),
                GzipCompress(Array.Empty<byte>()),
                GzipCompress(Slice(s_payload, 7, s_payload.Length - 7)),
                GzipCompress(Array.Empty<byte>())
            };

            AssertDecodes("gzip", Concat(parts.ToArray()), s_payload, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GzipMember_WithOptionalHeaderFields_Decodes(bool buffered)
        {
            // RFC 1952 section 2.3: FHCRC | FEXTRA | FNAME | FCOMMENT, all of which the decoder must skip.
            var header = new List<byte> { 0x1F, 0x8B, 0x08, 0x02 | 0x04 | 0x08 | 0x10, 0, 0, 0, 0, 0, 0xFF };
            header.AddRange(new byte[] { 3, 0, (byte)'a', (byte)'b', (byte)'c' });
            header.AddRange(new byte[] { (byte)'n', (byte)'a', (byte)'m', (byte)'e', 0 });
            header.AddRange(new byte[] { (byte)'c', 0 });
            header.AddRange(new byte[] { 0x12, 0x34 });
            var member = Concat(header.ToArray(), RawDeflateCompress(s_payload), GzipTrailer(s_payload));

            AssertDecodes("gzip", Concat(member, GzipCompress(s_payload)), Concat(s_payload, s_payload), buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Gzip_WithReservedHeaderFlag_Fails(bool buffered)
        {
            var wire = GzipCompress(s_payload);
            wire[3] |= 0x20;
            AssertFails("gzip", wire, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Gzip_WithTrailingGarbage_Fails(bool buffered)
        {
            AssertFails("gzip", Concat(GzipCompress(s_payload), new byte[] { 0x00, 0x01, 0x02 }), buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StackedGzipThenZlib_OverTwoMemberGzip_Decodes(bool buffered)
        {
            // "gzip, deflate": gzip applied first (two members), then zlib over the result.
            var gzip = Concat(GzipCompress(Slice(s_payload, 0, 1000)), GzipCompress(Slice(s_payload, 1000, s_payload.Length - 1000)));
            AssertDecodes("gzip, deflate", ZlibCompress(gzip), s_payload, buffered);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StackedZlibThenGzip_CorruptInnerAdler32_Fails(bool buffered)
        {
            // "deflate, gzip": zlib applied first, then gzip; the zlib layer's checksum fails under a valid gzip.
            var zlib = ZlibCompress(s_payload);
            zlib[zlib.Length - 2] ^= 0x10;
            AssertFails("deflate, gzip", GzipCompress(zlib), buffered);
        }

        [Test]
        public void StreamingTwoMemberGzip_InTinyChunks_Decodes()
        {
            AssertAsync.Run(async () =>
            {
                var wire = Concat(
                    GzipCompress(Slice(s_payload, 0, 30000)),
                    GzipCompress(Slice(s_payload, 30000, s_payload.Length - 30000)));
                var consumer = await DispatchStreamingAsync("gzip", wire, chunkSize: 7).ConfigureAwait(false);

                Assert.IsNull(consumer.Failure);
                CollectionAssert.AreEqual(s_payload, consumer.Decoded);
            });
        }

        private static void AssertDecodes(string contentEncoding, byte[] wire, byte[] expected, bool buffered)
        {
            AssertAsync.Run(async () =>
            {
                if (buffered)
                {
                    var outcome = await DispatchBufferedAsync(contentEncoding, wire).ConfigureAwait(false);
                    Assert.IsNull(outcome.Failure, "Buffered decode failed: " + outcome.Failure);
                    CollectionAssert.AreEqual(expected, outcome.Decoded);
                    return;
                }

                var consumer = await DispatchStreamingAsync(contentEncoding, wire).ConfigureAwait(false);
                Assert.IsNull(consumer.Failure, "Streaming decode failed: " + consumer.Failure);
                CollectionAssert.AreEqual(expected, consumer.Decoded);
            });
        }

        private static void AssertFails(string contentEncoding, byte[] wire, bool buffered)
        {
            AssertAsync.Run(async () =>
            {
                Exception failure;
                if (buffered)
                {
                    failure = (await DispatchBufferedAsync(contentEncoding, wire).ConfigureAwait(false)).Failure;
                }
                else
                {
                    failure = (await DispatchStreamingAsync(contentEncoding, wire).ConfigureAwait(false)).Failure;
                }

                AssertDecompressionFailure(failure);
            });
        }

        private static void AssertDecompressionFailure(Exception failure)
        {
            Assert.IsNotNull(failure, "A corrupt or truncated compressed body must fail.");
            for (var current = failure; current != null; current = current.InnerException)
            {
                if (current is UHttpException httpException)
                {
                    StringAssert.Contains("decompression", httpException.Message.ToLowerInvariant());
                    return;
                }
            }

            Assert.Fail("Expected a UHttpException decompression failure, got: " + failure);
        }

        private static async Task<BufferedOutcome> DispatchBufferedAsync(string contentEncoding, byte[] wire)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", contentEncoding);
            var transport = new MockTransport(HttpStatusCode.OK, headers, wire);
            var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/buffered"));

            try
            {
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                return new BufferedOutcome(response.Body.ToArray(), null);
            }
            catch (Exception ex) when (!(ex is AssertionException))
            {
                return new BufferedOutcome(null, ex);
            }
        }

        private static async Task<StreamingConsumer> DispatchStreamingAsync(
            string contentEncoding,
            byte[] wire,
            int chunkSize = 1024)
        {
            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", contentEncoding);
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        headers,
                        new MockResponseBodySource(
                            Chunk(wire, chunkSize),
                            length: wire.Length,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx)
                    .AsTask();
            });

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/streaming"));
            var consumer = new StreamingConsumer();
            await new DecompressionInterceptor()
                .Wrap(transport.DispatchAsync)(request, consumer, new RequestContext(request), CancellationToken.None)
                .ConfigureAwait(false);
            return consumer;
        }

        private static byte[] CreatePayload(int length)
        {
            // Deterministic, poorly compressible bytes so the DEFLATE data spans many blocks and input chunks.
            var bytes = new byte[length];
            uint state = 2463534242u;
            for (var i = 0; i < bytes.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                bytes[i] = (byte)(state & 0x3F);
            }

            return bytes;
        }

        internal static byte[] GzipCompress(byte[] input)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(input, 0, input.Length);

            return stream.ToArray();
        }

        internal static byte[] RawDeflateCompress(byte[] input)
        {
            using var stream = new MemoryStream();
            using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(input, 0, input.Length);

            return stream.ToArray();
        }

        /// <summary>RFC 1950: CMF 0x78 (CM 8, 32 KB window), FLG 0x9C, raw DEFLATE, big-endian Adler-32.</summary>
        internal static byte[] ZlibCompress(byte[] input)
        {
            return Concat(new byte[] { 0x78, 0x9C }, RawDeflateCompress(input), BigEndian(Adler32(input)));
        }

        private static byte[] GzipTrailer(byte[] data)
        {
            var crc = Crc32(data);
            var size = (uint)data.Length;
            return new[]
            {
                (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24),
                (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24)
            };
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

        private static byte[] BigEndian(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static byte[] Slice(byte[] bytes, int offset, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(bytes, offset, slice, 0, length);
            return slice;
        }

        private static byte[] Cut(byte[] bytes, int length)
        {
            return Slice(bytes, 0, length);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts)
                total += part.Length;

            var result = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private static IEnumerable<ReadOnlyMemory<byte>> Chunk(byte[] bytes, int chunkSize)
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                yield return new ReadOnlyMemory<byte>(bytes, offset, Math.Min(chunkSize, bytes.Length - offset));
        }

        private sealed class BufferedOutcome
        {
            internal BufferedOutcome(byte[] decoded, Exception failure)
            {
                Decoded = decoded;
                Failure = failure;
            }

            internal byte[] Decoded { get; }

            internal Exception Failure { get; }
        }

        private sealed class StreamingConsumer : IHttpHandler
        {
            internal Exception Failure { get; private set; }

            internal byte[] Decoded { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                try
                {
                    Assert.IsFalse(body.TryGetBufferedData(out _), "Expected the streaming decompression path.");
                    using var output = new MemoryStream();
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        output.Write(buffer, 0, read);
                    }

                    Decoded = output.ToArray();
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    Failure = ex;
                }
                finally
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Failure = error;
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _dispatch(request, handler, context, cancellationToken);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
