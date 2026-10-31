using System;
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    /// <summary>
    /// #65: LoggingInterceptor redacts URL query values and the shared sensitive header list by default.
    /// </summary>
    public class LoggingRedactionTests
    {
        private const string Secret = "s3cr3t-value-123";

        [TestCase(LoggingInterceptor.LogLevel.Minimal)]
        [TestCase(LoggingInterceptor.LogLevel.Standard)]
        [TestCase(LoggingInterceptor.LogLevel.Detailed)]
        public void QueryValuesAreRedactedAtEveryLogLevel(LoggingInterceptor.LogLevel level)
        {
            var captured = Capture(
                sink => new LoggingInterceptor(sink, level),
                "https://api.example.com/items?access_token=" + Secret + "&page=2");

            Assert.That(captured.Count, Is.GreaterThanOrEqualTo(2));
            foreach (var line in captured)
            {
                Assert.That(line, Does.Not.Contain(Secret));
                Assert.That(line, Does.Not.Contain("page=2"));
            }

            Assert.That(captured[0], Does.Contain("access_token=[REDACTED]&page=[REDACTED]"));
            Assert.That(captured[0], Does.Contain("https://api.example.com/items?"));
        }

        [Test]
        public void ErrorLineRedactsQueryValues()
        {
            var transport = new MockTransport();
            transport.EnqueueError(new UHttpError(UHttpErrorType.NetworkError, "boom"));
            var captured = new List<string>();
            var interceptor = new LoggingInterceptor(captured.Add, LoggingInterceptor.LogLevel.Standard);
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.com/x?key=" + Secret));

            AssertAsync.Run(async () =>
            {
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { interceptor }, transport);
                try
                {
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                }
                catch (UHttpException)
                {
                    // The error line is what this test checks.
                }
            });

            Assert.That(captured, Is.Not.Empty);
            foreach (var line in captured)
                Assert.That(line, Does.Not.Contain(Secret));
        }

        [Test]
        public void UserInfoIsRedacted()
        {
            var captured = Capture(
                sink => new LoggingInterceptor(sink),
                "https://user:" + Secret + "@api.example.com/items");

            foreach (var line in captured)
                Assert.That(line, Does.Not.Contain(Secret));
            Assert.That(captured[0], Does.Contain("https://[REDACTED]@api.example.com/items"));
        }

        [Test]
        public void QueryRedactionOptOutKeepsRawUrl()
        {
            var captured = Capture(
                sink => new LoggingInterceptor(
                    sink,
                    redactionPolicy: RedactionPolicy.Default.WithQueryValueRedaction(false)),
                "https://api.example.com/items?page=2");

            Assert.That(captured[0], Does.Contain("https://api.example.com/items?page=2"));
        }

        [TestCase("Authorization")]
        [TestCase("Proxy-Authorization")]
        [TestCase("Cookie")]
        [TestCase("X-Api-Key")]
        [TestCase("X-Auth-Token")]
        [TestCase("X-Amz-Security-Token")]
        [TestCase("X-Goog-Api-Key")]
        [TestCase("Api-Key")]
        [TestCase("X-CSRF-Token")]
        public void ExtendedRequestHeaderListIsRedactedByDefault(string headerName)
        {
            var headers = new HttpHeaders();
            headers.Set(headerName, Secret);
            headers.Set("Accept", "application/json");

            var captured = Capture(
                sink => new LoggingInterceptor(sink, LoggingInterceptor.LogLevel.Detailed, logHeaders: true),
                "https://api.example.com/items",
                headers);

            var all = string.Join("\n", captured);
            Assert.That(all, Does.Not.Contain(Secret));
            Assert.That(all, Does.Contain(headerName + ": [REDACTED]"));
            Assert.That(all, Does.Contain("Accept: application/json"));
        }

        [TestCase("Set-Cookie")]
        [TestCase("WWW-Authenticate")]
        [TestCase("Proxy-Authenticate")]
        public void ExtendedResponseHeaderListIsRedactedByDefault(string headerName)
        {
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set(headerName, Secret);
            var transport = new MockTransport();
            transport.EnqueueResponse(HttpStatusCode.OK, responseHeaders);

            var captured = Capture(
                sink => new LoggingInterceptor(sink, LoggingInterceptor.LogLevel.Detailed, logHeaders: true),
                transport,
                new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.com/items")));

            var all = string.Join("\n", captured);
            Assert.That(all, Does.Not.Contain(Secret));
            Assert.That(all, Does.Contain(headerName + ": [REDACTED]"));
        }

        [Test]
        public void SensitiveHeaderListIsConfigurable()
        {
            var headers = new HttpHeaders();
            headers.Set("X-Custom-Secret", Secret);
            headers.Set("Authorization", "Bearer visible-on-purpose");

            var policy = RedactionPolicy.Default
                .WithSensitiveHeaders("X-Custom-Secret")
                .WithoutSensitiveHeaders("Authorization");

            var captured = Capture(
                sink => new LoggingInterceptor(
                    sink,
                    LoggingInterceptor.LogLevel.Detailed,
                    logHeaders: true,
                    redactionPolicy: policy),
                "https://api.example.com/items",
                headers);

            var all = string.Join("\n", captured);
            Assert.That(all, Does.Not.Contain(Secret));
            Assert.That(all, Does.Contain("X-Custom-Secret: [REDACTED]"));
            Assert.That(all, Does.Contain("Authorization: Bearer visible-on-purpose"));
            Assert.IsTrue(RedactionPolicy.Default.IsSensitiveHeader("authorization"),
                "deriving a policy must not change the shared default");
        }

        [Test]
        public void RedactUrl_KeepsNamesFlagsAndFragment()
        {
            var policy = RedactionPolicy.Default;
            Assert.AreEqual(
                "https://h/p?a=[REDACTED]&flag&empty=#frag",
                policy.RedactUrl("https://h/p?a=1&flag&empty=#frag"));
            Assert.AreEqual("https://h/p", policy.RedactUrl("https://h/p"));
            Assert.AreEqual("/relative?x=[REDACTED]", policy.RedactUrl("/relative?x=y"));
            Assert.IsNull(policy.RedactUrl((Uri)null));
            Assert.AreEqual("https://h/p?a=1", RedactionPolicy.None.RedactUrl("https://h/p?a=1"));
        }

        private static List<string> Capture(
            Func<Action<string>, LoggingInterceptor> create,
            string url,
            HttpHeaders headers = null)
        {
            return Capture(create, new MockTransport(), new UHttpRequest(HttpMethod.GET, new Uri(url), headers));
        }

        private static List<string> Capture(
            Func<Action<string>, LoggingInterceptor> create,
            MockTransport transport,
            UHttpRequest request)
        {
            var captured = new List<string>();
            var interceptor = create(captured.Add);
            AssertAsync.Run(async () =>
            {
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { interceptor }, transport);
                await pipeline.ExecuteAsync(request, new RequestContext(request));
            });
            return captured;
        }
    }
}
