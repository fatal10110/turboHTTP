using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Phase 22a.6 (#102) variants of the streaming read gates the main fixture does not reach: an HTTP/2
    /// response that ends with a trailing HEADERS section (RFC 9113 section 8.1), and the <c>deflate</c>
    /// content coding (RFC 9110 section 8.4.1.2, zlib format), which shares the inflater with <c>gzip</c> but
    /// not its framing code.
    /// </summary>
    public sealed partial class StreamingAllocationGateTests
    {
        private const string TrailerName = "x-phase22a-checksum";
        private const string TrailerValue = "c0ffee";

        private static readonly byte[] s_deflatePayload = CompressZlib(s_decompressedPayload);

        // Same per-read budget as the trailer-free HTTP/2 read: trailers arrive once, after the data, and
        // must not add a per-read cost.
        [UnityTest]
        public IEnumerator Http2StreamingReadWithTrailers_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            return MeasureScenario(
                CreateHttp2TrailersTarget,
                new AllocationBudget(precise: 0, optimizedFrame: 32, unoptimizedMonoFrame: 416, unoptimizedIl2CppFrame: 432));
        }

        // Same known allocation point and budget as the gzip read (the inflater's Task<int> per read).
        [UnityTest]
        public IEnumerator DeflateDecompressionStreamingRead_KnownAllocationPoint_StaysWithinBudget()
        {
            return MeasureScenario(
                CreateDeflateDecompressionTarget,
                new AllocationBudget(precise: 384, optimizedFrame: 4352, unoptimizedMonoFrame: 4384, unoptimizedIl2CppFrame: 4160));
        }

        private static MeasuredReadTarget CreateHttp2TrailersTarget()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://phase22a.local/http2-trailers"));
            var context = new RequestContext(request);
            var connection = new Http2Connection(
                Stream.Null,
                "phase22a.local",
                443,
                new Http2Options(),
                new StreamingOptions
                {
                    DefaultHttp2PerStreamReceiveBufferBytes = Http2ChunkBytes * Http2ChunkCount
                });
            var stream = new Http2Stream();
            stream.Initialize(1, request, NoOpHttpHandler.Instance, context, 65535, 65535, connection);

            // No declared length: a response with trailers is typically framed by END_STREAM alone.
            var source = new Http2ResponseBodySource(connection, stream, length: null, completed: false);
            for (var i = 0; i < Http2ChunkCount; i++)
            {
                Assert.AreEqual(
                    Http2ResponseBodyEnqueueResult.Accepted,
                    source.TryEnqueueData(s_http2Chunk, 0, s_http2Chunk.Length, flowControlledLength: 0));
            }

            // Http2Stream's order for a trailing HEADERS frame with END_STREAM: trailers, then completion.
            var trailers = new HttpHeaders();
            trailers.Set(TrailerName, TrailerValue);
            source.SetTrailers(trailers);
            source.Complete();

            return new MeasuredReadTarget(
                "http2_streaming_body_source_read_with_trailers",
                Http2ChunkBytes,
                buffer => source.ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult(),
                () =>
                {
                    try
                    {
                        var received = source.GetTrailersAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Assert.AreEqual(TrailerValue, received.Get(TrailerName), "the trailers must reach the reader");
                        source.DisposeAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        request.Dispose();
                        connection.Dispose();
                    }
                });
        }

        private static MeasuredReadTarget CreateDeflateDecompressionTarget()
        {
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new CallbackTransport(
                    (request, handler, context, cancellationToken) =>
                    {
                        handler.OnRequestStart(request, context);

                        var headers = new HttpHeaders();
                        headers.Set("Content-Encoding", "deflate");
                        headers.Set("Content-Length", s_deflatePayload.Length.ToString());

                        return handler.OnResponseStartAsync(
                                (int)HttpStatusCode.OK,
                                headers,
                                new MockResponseBodySource(
                                    ChunkPayload(s_deflatePayload, 512),
                                    length: s_deflatePayload.Length,
                                    trailers: HttpHeaders.Empty,
                                    exposeBufferedData: false),
                                context)
                            .AsTask();
                    }),
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor> { new DecompressionInterceptor() }
            });

            UHttpStreamingResponse response = null;
            try
            {
                response = client.Get("https://phase22a.local/deflate").SendStreamingAsync().GetAwaiter().GetResult();
                Assert.IsNull(
                    response.Headers.Get("Content-Encoding"),
                    "the decoded response must not keep the deflate content coding");
            }
            catch
            {
                response?.Dispose();
                client.Dispose();
                throw;
            }

            var bodySource = ExtractBodySource(response);
            long decoded = 0;
            return new MeasuredReadTarget(
                "deflate_decompression_streaming_body_source_read",
                DecompressionChunkBytes,
                buffer =>
                {
                    var read = bodySource.ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                    decoded += read;
                    return read;
                },
                () =>
                {
                    try
                    {
                        Assert.AreEqual(s_decompressedPayload.Length, decoded, "deflate decoded length");
                        response.DisposeAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
        }

        /// <summary>The zlib format (RFC 1950): header, raw DEFLATE data, Adler-32 of the payload.</summary>
        private static byte[] CompressZlib(byte[] payload)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(payload, 0, payload.Length);
            }

            uint a = 1, b = 0;
            foreach (var value in payload)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            var adler = (b << 16) | a;
            output.WriteByte((byte)(adler >> 24));
            output.WriteByte((byte)(adler >> 16));
            output.WriteByte((byte)(adler >> 8));
            output.WriteByte((byte)adler);
            return output.ToArray();
        }
    }
}
