using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Middleware;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// RFC 9111 section 3.2: freshening a stored response from a 304 never updates Content-Length, and a cache
    /// that stores decoded content keeps its own Content-Encoding (none) when the 304 repeats the coding.
    /// </summary>
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_Revalidation304WithContentEncoding_KeepsDecodedEntryUnlabelled()
        {
            AssertAsync.Run(async () =>
            {
                const string plain = "decoded body stored by the cache";
                var gzipBody = GzipBytes(Encoding.UTF8.GetBytes(plain));
                var compressedLength = gzipBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var cache = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"v1\"");
                    headers.Set("Content-Encoding", "gzip");
                    headers.Set("Content-Length", compressedLength);
                    handler.OnRequestStart(req, ctx);
                    if (call == 1)
                    {
                        headers.Set("Cache-Control", "max-age=0, must-revalidate");
                        return handler.OnResponseStartAsync(
                                200,
                                headers,
                                new BufferedResponseBodySource(gzipBody, HttpHeaders.Empty),
                                ctx)
                            .AsTask();
                    }

                    headers.Set("Cache-Control", "max-age=60");
                    return handler.OnResponseStartAsync(
                            304,
                            headers,
                            new BufferedResponseBodySource(Array.Empty<byte>(), HttpHeaders.Empty),
                            ctx)
                        .AsTask();
                });

                // Cache outer, Decompression inner: the cache stores the decoded body without coding fields.
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache, new DecompressionInterceptor() },
                    transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/decoded-304"));

                using (var first = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(plain, first.GetBodyAsString());
                    Assert.IsNull(first.Headers.Get("Content-Encoding"));
                }

                using var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                AssertDecodedEntry(revalidated, plain, compressedLength);

                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount, "The 304's max-age=60 must make the freshened entry a HIT.");
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                AssertDecodedEntry(hit, plain, compressedLength);
            });
        }

        private static void AssertDecodedEntry(UHttpResponse response, string plain, string compressedLength)
        {
            Assert.AreEqual(plain, response.GetBodyAsString());
            Assert.IsNull(response.Headers.Get("Content-Encoding"), "A 304's Content-Encoding must not relabel decoded content.");
            Assert.AreNotEqual(
                compressedLength,
                response.Headers.Get("Content-Length"),
                "A 304's Content-Length must not replace the stored entry's length.");
        }

        private static byte[] GzipBytes(byte[] input)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(input, 0, input.Length);

            return stream.ToArray();
        }
    }
}
