using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// #795: redirect exception messages carry at most scheme and host, and the Redirect timeline event
    /// and the RedirectChain state never carry user-info or query values of a hop.
    /// </summary>
    [TestFixture]
    public class RedirectRedactionTests
    {
        private const string Password = "PASS-SECRET-5a9";
        private const string Token = "TOKEN-SECRET-c42";

        [Test]
        public void FollowedRedirect_EventAndChainCarryNoUserInfoOrQuerySecret()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                        return Redirect(req, ctx, "https://user:" + Password + "@example.test/next?token=" + Token);

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                });
                var pipeline = new TestInterceptorPipeline(new[] { new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start?token=" + Token));
                var context = new RequestContext(request);

                var response = await pipeline.ExecuteAsync(request, context);
                response.Dispose();

                var found = false;
                foreach (var evt in context.Timeline)
                {
                    if (evt.Name != "Redirect")
                        continue;
                    found = true;
                    Assert.AreEqual("https://example.test/start?token=[REDACTED]", evt.Data["from"]);
                    Assert.AreEqual("https://[REDACTED]@example.test/next?token=[REDACTED]", evt.Data["to"]);
                }

                Assert.IsTrue(found, "Redirect event recorded");
                var chain = context.GetState<string[]>("RedirectChain");
                Assert.IsNotNull(chain);
                Assert.AreEqual(2, chain.Length);
                foreach (var hop in chain)
                {
                    Assert.That(hop, Does.Not.Contain(Password));
                    Assert.That(hop, Does.Not.Contain(Token));
                }
            });
        }

        [Test]
        public void BlockedDowngrade_MessageCarriesOnlySchemeAndHost()
        {
            var ex = RunExpectingError(req =>
                "http://user:" + Password + "@example.test/insecure?token=" + Token);

            StringAssert.Contains("Blocked insecure redirect downgrade", ex.Message);
            StringAssert.Contains("'http://example.test'", ex.Message);
            AssertNoSecret(ex);
        }

        [Test]
        public void RedirectLoop_MessageCarriesOnlySchemeAndHost()
        {
            var ex = RunExpectingError(req =>
                req.Uri.AbsolutePath == "/start"
                    ? "https://example.test/a?token=" + Token
                    : "/start?token=" + Token);

            StringAssert.Contains("Redirect loop detected", ex.Message);
            AssertNoSecret(ex);
        }

        [Test]
        public void InvalidLocation_MessageOmitsTheLocationValue()
        {
            var ex = RunExpectingError(req => "http://user:" + Password + "@[bad/?token=" + Token);

            StringAssert.Contains("Invalid redirect Location header", ex.Message);
            AssertNoSecret(ex);
        }

        private static UHttpException RunExpectingError(Func<UHttpRequest, string> location)
        {
            UHttpException error = null;
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) => Redirect(req, ctx, location(req)));
                var pipeline = new TestInterceptorPipeline(
                    new[] { new RedirectInterceptor(defaultFollowRedirects: true, defaultMaxRedirects: 10) },
                    transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start?token=" + Token));
                error = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));
            });

            Assert.IsNotNull(error);
            return error;
        }

        private static Task<UHttpResponse> Redirect(UHttpRequest req, RequestContext ctx, string location)
        {
            var headers = new HttpHeaders();
            headers.Set("Location", location);
            return Task.FromResult(new UHttpResponse(
                HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req));
        }

        private static void AssertNoSecret(UHttpException ex)
        {
            var text = ex.ToString() + "\n" + ex.HttpError;
            Assert.That(text, Does.Not.Contain(Password));
            Assert.That(text, Does.Not.Contain(Token));
        }
    }
}
