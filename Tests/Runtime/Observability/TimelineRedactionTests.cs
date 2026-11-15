using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    /// <summary>
    /// #1059: Monitor timeline values and error text pass through the redaction policy; LoggingHandler
    /// error lines carry no raw URL. #1067: URL-valued headers (Location, Content-Location, Referer, Link,
    /// Refresh) are query-redacted in the Monitor capture and the logging sink.
    /// </summary>
    public class TimelineRedactionTests
    {
        private const string Secret = "TOKEN-SECRET-81c";
        private const string Secret2 = "CODE-SECRET-27d";
        private const string Secret3 = "SIG-SECRET-6e0";

        [SetUp]
        public void SetUp()
        {
            Reset();
        }

        [TearDown]
        public void TearDown()
        {
            Reset();
            ObservabilityModuleRegistration.EnsureRegistered();
        }

        [Test]
        public void CacheMissAndRedirect_TimelineCarriesNoQuerySecret()
        {
            var transport = new MockTransport((req, ctx, ct) =>
            {
                if (req.Uri.AbsolutePath == "/start")
                {
                    var redirectHeaders = new HttpHeaders();
                    redirectHeaders.Set("Location", "/final?access_token=" + Secret2 + "&page=1");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found, redirectHeaders, Array.Empty<byte>(), ctx.Elapsed, req));
                }

                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), Encoding.UTF8.GetBytes("done"), ctx.Elapsed, req));
            });

            var events = new List<HttpMonitorEvent>();
            using (MonitorInterceptor.AttachViewer())
            {
                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://api.example.test/start?access_token=" + Secret));
                AssertAsync.Run(async () =>
                {
                    var pipeline = new TestInterceptorPipeline(
                        new IHttpInterceptor[]
                        {
                            new MonitorInterceptor(),
                            new RedirectInterceptor(),
                            new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() })
                        },
                        transport);
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                });
                MonitorInterceptor.GetHistorySnapshot(events);
            }

            Assert.That(events, Is.Not.Empty);
            var sawRedirect = false;
            var sawCacheMiss = false;
            foreach (var evt in events)
            {
                AssertNoSecret(evt, Secret, Secret2);
                foreach (var timelineEvent in evt.Timeline)
                {
                    sawRedirect |= timelineEvent.Name == "Redirect";
                    sawCacheMiss |= timelineEvent.Name == "CacheMiss";
                }
            }

            Assert.IsTrue(sawRedirect, "the Redirect timeline event is captured");
            Assert.IsTrue(sawCacheMiss, "the CacheMiss timeline event is captured");
        }

        [Test]
        public void CustomTimelineValuesAndErrorText_AreRedactedAtCapture()
        {
            var transport = new MockTransport();
            transport.EnqueueError(new UHttpError(
                UHttpErrorType.NetworkError,
                "connect failed for 'https://user:" + Secret3 + "@api.example.test/x?access_token=" + Secret + "'"));

            var events = new List<HttpMonitorEvent>();
            using (MonitorInterceptor.AttachViewer())
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/x"));
                AssertAsync.Run(async () =>
                {
                    var pipeline = new TestInterceptorPipeline(
                        new IHttpInterceptor[] { new MonitorInterceptor(), new RecordingInterceptor() },
                        transport);
                    try
                    {
                        await pipeline.ExecuteAsync(request, new RequestContext(request));
                    }
                    catch (UHttpException)
                    {
                        // The captured error text is what this test checks.
                    }
                });
                MonitorInterceptor.GetHistorySnapshot(events);
            }

            Assert.AreEqual(1, events.Count);
            var captured = events[0];
            AssertNoSecret(captured, Secret, Secret2, Secret3);
            StringAssert.Contains("access_token=[REDACTED]", captured.Error);

            var custom = FindTimelineEvent(captured, "Custom");
            Assert.AreEqual(RedactionPolicy.RedactedValue, custom.Data["Authorization"]);
            Assert.AreEqual("see https://cdn.example.test/a?code=[REDACTED]&page=[REDACTED]", custom.Data["note"]);
        }

        [Test]
        public void RedactionNone_KeepsTimelineValuesRaw()
        {
            MonitorInterceptor.RedactionPolicy = RedactionPolicy.None;
            var events = new List<HttpMonitorEvent>();
            using (MonitorInterceptor.AttachViewer())
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK, null, null);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/x"));
                AssertAsync.Run(async () =>
                {
                    var pipeline = new TestInterceptorPipeline(
                        new IHttpInterceptor[] { new MonitorInterceptor(), new RecordingInterceptor() },
                        transport);
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                });
                MonitorInterceptor.GetHistorySnapshot(events);
            }

            var custom = FindTimelineEvent(events[0], "Custom");
            StringAssert.Contains("code=" + Secret2, custom.Data["note"]);
        }

        [Test]
        public void UrlValuedResponseHeaders_AreQueryRedactedInMonitorCapture()
        {
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set("Location", "https://cdn.example.test/o?X-Amz-Signature=" + Secret3 + "&code=" + Secret2);
            responseHeaders.Set("Content-Location", "/c?access_token=" + Secret);
            responseHeaders.Set("Link", "<https://api.example.test/p?token=" + Secret + ">; rel=\"next\"");
            responseHeaders.Set("Refresh", "5; url=https://api.example.test/r?code=" + Secret2);
            responseHeaders.Set("Content-Type", "text/plain");
            var requestHeaders = new HttpHeaders();
            requestHeaders.Set("Referer", "https://app.example.test/page?session=" + Secret);

            var events = new List<HttpMonitorEvent>();
            using (MonitorInterceptor.AttachViewer())
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.Found, responseHeaders, null);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/x"), requestHeaders);
                AssertAsync.Run(async () =>
                {
                    var pipeline = new TestInterceptorPipeline(
                        new IHttpInterceptor[] { new MonitorInterceptor() },
                        transport);
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                });
                MonitorInterceptor.GetHistorySnapshot(events);
            }

            Assert.AreEqual(1, events.Count);
            var evt = events[0];
            AssertNoSecret(evt, Secret, Secret2, Secret3);
            Assert.AreEqual(
                "https://cdn.example.test/o?X-Amz-Signature=[REDACTED]&code=[REDACTED]",
                evt.ResponseHeaders["Location"]);
            Assert.AreEqual("<https://api.example.test/p?token=[REDACTED]>; rel=\"next\"", evt.ResponseHeaders["Link"]);
            Assert.AreEqual("5; url=https://api.example.test/r?code=[REDACTED]", evt.ResponseHeaders["Refresh"]);
            Assert.AreEqual("/c?access_token=[REDACTED]", evt.ResponseHeaders["Content-Location"]);
            Assert.AreEqual("https://app.example.test/page?session=[REDACTED]", evt.RequestHeaders["Referer"]);
            Assert.AreEqual("text/plain", evt.ResponseHeaders["Content-Type"]);
        }

        [Test]
        public void LoggingSink_RedactsLocationHeaderAndErrorUrls()
        {
            var responseHeaders = new HttpHeaders();
            responseHeaders.Set("Location", "https://cdn.example.test/o?X-Amz-Signature=" + Secret3 + "&code=" + Secret2);
            var transport = new MockTransport();
            transport.EnqueueResponse(HttpStatusCode.Found, responseHeaders, null);
            transport.EnqueueError(new UHttpError(
                UHttpErrorType.NetworkError,
                "Redirect target https://api.example.test/next?access_token=" + Secret + " failed"));

            var captured = new List<string>();
            var interceptor = new LoggingInterceptor(
                captured.Add,
                LoggingInterceptor.LogLevel.Detailed,
                logHeaders: true);
            AssertAsync.Run(async () =>
            {
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { interceptor }, transport);
                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/a"));
                await pipeline.ExecuteAsync(first, new RequestContext(first));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://api.example.test/b"));
                try
                {
                    await pipeline.ExecuteAsync(second, new RequestContext(second));
                }
                catch (UHttpException)
                {
                    // The error line is what this test checks.
                }
            });

            var all = string.Join("\n", captured);
            Assert.That(all, Does.Not.Contain(Secret));
            Assert.That(all, Does.Not.Contain(Secret2));
            Assert.That(all, Does.Not.Contain(Secret3));
            StringAssert.Contains("Location: https://cdn.example.test/o?X-Amz-Signature=[REDACTED]&code=[REDACTED]", all);
            StringAssert.Contains("access_token=[REDACTED]", all);
        }

        [Test]
        public void Policy_RedactsUrlValuedHeadersAndUrlsInText()
        {
            var policy = RedactionPolicy.Default;
            Assert.AreEqual(
                "https://[REDACTED]@h.test/p?a=[REDACTED]#f",
                policy.RedactHeaderValue("location", "https://u:p@h.test/p?a=1#f"));
            Assert.AreEqual(
                "<https://h.test/1?a=[REDACTED]>; rel=next, <https://h.test/2>; rel=last",
                policy.RedactHeaderValue("Link", "<https://h.test/1?a=1>; rel=next, <https://h.test/2>; rel=last"));
            Assert.AreEqual("0;URL=/x?t=[REDACTED]", policy.RedactHeaderValue("Refresh", "0;URL=/x?t=1"));
            Assert.AreEqual("0; url='/x?t=[REDACTED]'", policy.RedactHeaderValue("Refresh", "0; url='/x?t=1'"));
            Assert.AreEqual("a=1?b=2", policy.RedactHeaderValue("X-Other", "a=1?b=2"));
            Assert.AreEqual(
                "https://h.test/?t=1",
                policy.WithQueryValueRedaction(false).RedactHeaderValue("Location", "https://h.test/?t=1"));

            Assert.AreEqual(
                "loop at 'https://h.test/a?t=[REDACTED]' and http://[REDACTED]@g.test/ end",
                policy.RedactText("loop at 'https://h.test/a?t=1' and http://u:p@g.test/ end"));
            var plain = "no url here?x=1";
            Assert.AreSame(plain, policy.RedactText(plain));
            Assert.AreEqual("https://h.test/?t=1", RedactionPolicy.None.RedactText("https://h.test/?t=1"));
        }

        private static HttpMonitorTimelineEvent FindTimelineEvent(HttpMonitorEvent evt, string name)
        {
            foreach (var timelineEvent in evt.Timeline)
            {
                if (timelineEvent.Name == name)
                    return timelineEvent;
            }

            Assert.Fail("timeline event " + name + " not captured");
            return null;
        }

        private static void AssertNoSecret(HttpMonitorEvent evt, params string[] secrets)
        {
            var builder = new StringBuilder();
            builder.Append(evt.Url).Append('\n').Append(evt.Error).Append('\n');
            foreach (var pair in evt.RequestHeaders)
                builder.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
            foreach (var pair in evt.ResponseHeaders)
                builder.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
            foreach (var timelineEvent in evt.Timeline)
            {
                builder.Append(timelineEvent.Name).Append('\n');
                if (timelineEvent.Data == null)
                    continue;
                foreach (var pair in timelineEvent.Data)
                    builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            }

            var text = builder.ToString();
            foreach (var secret in secrets)
                Assert.That(text, Does.Not.Contain(secret), "captured event contains " + secret);
        }

        private static void Reset()
        {
            MonitorInterceptor.CaptureEnabled = true;
            MonitorInterceptor.CaptureWithoutViewer = false;
            MonitorInterceptor.RedactionPolicy = null;
            MonitorInterceptor.HeaderValueTransform = null;
            MonitorInterceptor.DiagnosticLogger = null;
            MonitorInterceptor.ClearHistory();
        }

        private sealed class RecordingInterceptor : IHttpInterceptor
        {
            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    context.RecordEvent("Custom", new Dictionary<string, object>
                    {
                        { "Authorization", "Bearer " + Secret },
                        { "note", "see https://cdn.example.test/a?code=" + Secret2 + "&page=2" }
                    });
                    return next(request, handler, context, cancellationToken);
                };
            }
        }
    }
}
