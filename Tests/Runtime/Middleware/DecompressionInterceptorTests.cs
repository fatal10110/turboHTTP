using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    public class DecompressionInterceptorTests
    {
        [Test]
        public void GzipResponse_IsDecompressed_AndEncodingHeadersRemoved()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("compressed-payload");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Length", compressed.Length.ToString());

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip, deflate", transport.LastRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("compressed-payload", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
                Assert.IsFalse(response.Headers.Contains("Content-Length"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ExistingAcceptEncoding_IsPreserved()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("already-configured");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Accept-Encoding", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"), requestHeaders);

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("gzip", transport.LastRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual("already-configured", response.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void UncompressedResponse_PassesThroughUnchanged()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("plain-payload");
                var headers = new HttpHeaders();
                headers.Set("Content-Type", "text/plain");

                var transport = new MockTransport(HttpStatusCode.OK, headers, body);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/plain"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("plain-payload", response.GetBodyAsString());
                Assert.AreEqual("text/plain", response.Headers.Get("Content-Type"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CorruptGzip_ReportsResponseError()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("not-gzip"));
            var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/corrupt"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.Message, Does.Contain("decompression"));
        }

        [Test]
        public void TruncatedGzip_ReportsResponseError()
        {
            var body = Encoding.UTF8.GetBytes("truncated-payload");
            var compressed = Compress(body, useGzip: true);
            var truncated = new byte[compressed.Length - 2];
            Array.Copy(compressed, truncated, truncated.Length);

            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, truncated);
            var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/truncated"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.Message, Does.Contain("decompression"));
        }

        [Test]
        public void OversizedDecompressedResponse_ReportsResponseError()
        {
            var body = Encoding.UTF8.GetBytes(new string('x', 128));
            var compressed = Compress(body, useGzip: true);
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");

            var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
            var pipeline = new TestInterceptorPipeline(
                new[] { new DecompressionInterceptor(maxDecompressedBodySizeBytes: 32) },
                transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/oversized"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var _ = await pipeline.ExecuteAsync(request, context);
            });

            Assert.That(ex.HttpError.InnerException?.Message, Does.Contain("maximum size"));
        }

        [Test]
        public void XGzipAlias_IsDecompressed()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("alias-payload");
                var compressed = Compress(body, useGzip: true);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "x-gzip");

                var transport = new MockTransport(HttpStatusCode.OK, headers, compressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/x-gzip"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("alias-payload", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MultiValueContentEncoding_IsDecompressedInReverseOrder()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("double-encoded");
                var gzipCompressed = Compress(body, useGzip: true);
                var doubleCompressed = Compress(gzipCompressed, useGzip: false);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip, deflate");

                var transport = new MockTransport(HttpStatusCode.OK, headers, doubleCompressed);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/multi"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("double-encoded", response.GetBodyAsString());
                Assert.IsFalse(response.Headers.Contains("Content-Encoding"));
            }).GetAwaiter().GetResult();
        }

        private static byte[] Compress(byte[] input, bool useGzip)
        {
            using var stream = new MemoryStream();
            using (Stream compressionStream = useGzip
                       ? new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true)
                       : new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            {
                compressionStream.Write(input, 0, input.Length);
            }

            return stream.ToArray();
        }
    }
}
