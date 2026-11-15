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
    /// <summary>
    /// #478: cache timeline events carry the request key with query values redacted and never the raw
    /// query or the Vary part of the storage key (request header values such as API keys).
    /// </summary>
    public class CacheEventRedactionTests
    {
        private const string QuerySecret = "QUERY-SECRET-4b1";
        private const string ApiKeySecret = "APIKEY-SECRET-9c3";

        [Test]
        public void MissHitAndRevalidate_EventsCarryNoQueryOrVarySecret()
        {
            Task.Run(async () =>
            {
                var responseHeaders = new HttpHeaders();
                responseHeaders.Set("Cache-Control", "max-age=60");
                responseHeaders.Set("Vary", "X-Api-Key");
                responseHeaders.Set("ETag", "\"v1\"");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    responseHeaders,
                    Encoding.UTF8.GetBytes("payload"));
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://api.example.test/items?access_token=" + QuerySecret + "&page=2");

                var missContext = await SendAsync(pipeline, uri, noCache: false);
                var hitContext = await SendAsync(pipeline, uri, noCache: false);
                var revalidateContext = await SendAsync(pipeline, uri, noCache: true);

                const string expectedKey =
                    "GET https://api.example.test/items?access_token=[REDACTED]&page=[REDACTED]";
                Assert.AreEqual(expectedKey, FindKey(missContext, "CacheMiss"));
                Assert.AreEqual(expectedKey, FindKey(hitContext, "CacheHit"));
                Assert.AreEqual(expectedKey, FindKey(revalidateContext, "CacheRevalidate"));
                AssertNoSecrets(missContext);
                AssertNoSecrets(hitContext);
                AssertNoSecrets(revalidateContext);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void UnsafeInvalidation_EventCarriesNoQuerySecret()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>());
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://api.example.test/items?access_token=" + QuerySecret),
                    new HttpHeaders(),
                    Encoding.UTF8.GetBytes("{}"));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);
                response.Dispose();

                var found = false;
                foreach (var evt in context.Timeline)
                {
                    if (evt.Name != "CacheInvalidated")
                        continue;
                    found = true;
                    Assert.AreEqual("https://api.example.test/items?access_token=[REDACTED]", evt.Data["uri"]);
                }

                Assert.IsTrue(found, "CacheInvalidated recorded");
                AssertNoSecrets(context);
            }).GetAwaiter().GetResult();
        }

        private static async Task<RequestContext> SendAsync(TestInterceptorPipeline pipeline, Uri uri, bool noCache)
        {
            var headers = new HttpHeaders();
            headers.Set("X-Api-Key", ApiKeySecret);
            if (noCache)
                headers.Set("Cache-Control", "no-cache");
            var request = new UHttpRequest(HttpMethod.GET, uri, headers);
            var context = new RequestContext(request);
            var response = await pipeline.ExecuteAsync(request, context);
            response.Dispose();
            return context;
        }

        private static string FindKey(RequestContext context, string eventName)
        {
            foreach (var evt in context.Timeline)
            {
                if (evt.Name == eventName)
                    return evt.Data["key"] as string;
            }

            Assert.Fail(eventName + " not recorded");
            return null;
        }

        private static void AssertNoSecrets(RequestContext context)
        {
            foreach (var evt in context.Timeline)
            {
                if (evt.Data == null)
                    continue;
                foreach (var pair in evt.Data)
                {
                    var value = pair.Value?.ToString() ?? string.Empty;
                    Assert.That(value, Does.Not.Contain(QuerySecret), evt.Name + "." + pair.Key);
                    Assert.That(value, Does.Not.Contain(ApiKeySecret), evt.Name + "." + pair.Key);
                }
            }
        }
    }
}
