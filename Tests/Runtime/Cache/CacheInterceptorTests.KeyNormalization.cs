using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    public partial class CacheInterceptorTests
    {
        // RFC 9111 section 2 keys on the target URI; RFC 3986 section 6.2.2 allows only
        // syntax-based normalization (case, percent-encoding, dot segments). Empty path
        // segments, empty query parameters and query order are significant.
        [TestCase("https://example.test/a//b", "https://example.test/a/b")]
        [TestCase("https://example.test/a/b/", "https://example.test/a/b")]
        [TestCase("https://example.test/data?b=1&a=2", "https://example.test/data?a=2&b=1")]
        [TestCase("https://example.test/data?a=1&&b=2", "https://example.test/data?a=1&b=2")]
        [TestCase("https://example.test/data?a=1&", "https://example.test/data?a=1")]
        [TestCase("https://example.test/data?a", "https://example.test/data?a=")]
        [TestCase("https://example.test/data?a=1&a=2", "https://example.test/data?a=2&a=1")]
        public void CacheInterceptor_KeyNormalization_DistinctUrisStayDistinct(string first, string second)
        {
            Assert.AreNotEqual(
                CacheInterceptor.NormalizeUri(new Uri(first)),
                CacheInterceptor.NormalizeUri(new Uri(second)));
        }

        [TestCase("HTTPS://Example.TEST:443/p?q=1", "https://example.test/p?q=1")]
        [TestCase("http://example.test:80/p", "http://example.test/p")]
        [TestCase("https://example.test/%7euser?x=%7e", "https://example.test/~user?x=~")]
        [TestCase("https://example.test/a%2fb?x=%2f", "https://example.test/a%2Fb?x=%2F")]
        [TestCase("https://example.test/p?x=%41", "https://example.test/p?x=A")]
        [TestCase("https://example.test/a/./b/../c", "https://example.test/a/c")]
        [TestCase("https://example.test/p#frag", "https://example.test/p")]
        public void CacheInterceptor_KeyNormalization_SyntaxEquivalentUrisShareKey(string first, string second)
        {
            Assert.AreEqual(
                CacheInterceptor.NormalizeUri(new Uri(second)),
                CacheInterceptor.NormalizeUri(new Uri(first)));
        }

        [Test]
        public void CacheInterceptor_KeyNormalization_KeepsQueryOrderAndEmptySegments()
        {
            Assert.AreEqual(
                "https://example.test/a//b/?b=1&&a=2",
                CacheInterceptor.NormalizeUri(new Uri("https://example.test/a//b/?b=1&&a=2")));
        }

        [Test]
        public void CacheInterceptor_KeyNormalization_EmptyPathSegmentIsSeparateCacheEntry()
        {
            Task.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("payload"));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a/b"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a//b"));

                await pipeline.ExecuteAsync(requestA, new RequestContext(requestA));
                var second = await pipeline.ExecuteAsync(requestB, new RequestContext(requestB));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreNotEqual("HIT", second.Headers.Get("X-Cache"));
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }
    }
}
