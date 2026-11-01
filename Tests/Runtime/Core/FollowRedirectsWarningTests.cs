using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #64: FollowRedirects is acted on only by the Middleware module's
    /// RedirectInterceptor. With the module's interceptor configured redirects are followed and
    /// nothing is logged; without it the client warns once through the diagnostic sink.
    /// </summary>
    [TestFixture]
    public class FollowRedirectsWarningTests
    {
        private List<string> _messages;

        [SetUp]
        public void SetUp()
        {
            _messages = new List<string>();
            TurboDiagnostics.Reset();
            TurboDiagnostics.SetSink(message =>
            {
                lock (_messages)
                    _messages.Add(message);
            });
        }

        [TearDown]
        public void TearDown()
        {
            TurboDiagnostics.Reset();
        }

        [Test]
        public void ModuleAbsent_DefaultOptions_WarnOnceAndReturnTheRedirect()
        {
            AssertAsync.Run(async () =>
            {
                using (var client = CreateClient(null))
                using (var response = await client.Get("https://example.test/start").SendBufferedAsync())
                {
                    Assert.AreEqual(HttpStatusCode.Found, response.StatusCode, "nothing follows the redirect");
                }

                using (CreateClient(null))
                {
                }

                Assert.AreEqual(1, CountRedirectWarnings(), "the warning is shown once");
            });
        }

        [Test]
        public void ModulePresent_RedirectInterceptorConfigured_FollowsWithoutWarning()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(new RedirectInterceptor());
                using var response = await client.Get("https://example.test/start").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("done", response.GetBodyAsString());
                Assert.AreEqual(0, CountRedirectWarnings());
            });
        }

        [Test]
        public void FollowRedirectsDisabled_DoesNotWarn()
        {
            using (new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                DisposeTransport = true,
                FollowRedirects = false
            }))
            {
            }

            Assert.AreEqual(0, CountRedirectWarnings());
        }

        private int CountRedirectWarnings()
        {
            lock (_messages)
            {
                int count = 0;
                foreach (var message in _messages)
                {
                    if (message.Contains("FollowRedirects"))
                        count++;
                }

                return count;
            }
        }

        private static UHttpClient CreateClient(IHttpInterceptor redirect)
        {
            var transport = new MockTransport((req, ctx, ct) =>
            {
                if (req.Uri.AbsolutePath == "/start")
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", "/final");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                }

                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), Encoding.UTF8.GetBytes("done"), ctx.Elapsed, req));
            });

            var options = new UHttpClientOptions { Transport = transport, DisposeTransport = true };
            if (redirect != null)
                options.Interceptors.Add(redirect);
            return new UHttpClient(options);
        }
    }
}
