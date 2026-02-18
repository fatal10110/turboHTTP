using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    public class MonitorMiddlewareTests
    {
        [SetUp]
        public void SetUp()
        {
            ResetMonitor();
        }

        [TearDown]
        public void TearDown()
        {
            ResetMonitor();
        }

        [Test]
        public void CapturesSuccessfulRequestAndPreservesTimeline()
        {
            Task.Run(async () =>
            {
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Content-Type", "application/json");

                var responseHeaders = new HttpHeaders();
                responseHeaders.Set("Content-Type", "application/json");

                var requestBody = Encoding.UTF8.GetBytes("{\"name\":\"TurboHTTP\"}");
                var responseBody = Encoding.UTF8.GetBytes("{\"ok\":true}");

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://api.example.com/v1/items"),
                    requestHeaders,
                    requestBody);
                var context = new RequestContext(request);
                context.RecordEvent("CustomStage", new Dictionary<string, object>
                {
                    { "attempt", 1 }
                });

                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    responseHeaders,
                    responseBody);
                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[] { new MonitorMiddleware() },
                    transport);

                await pipeline.ExecuteAsync(request, context);

                requestBody[0] = (byte)'X';
                responseBody[0] = (byte)'X';

                var snapshot = new List<HttpMonitorEvent>();
                MonitorMiddleware.GetHistorySnapshot(snapshot);
                Assert.AreEqual(1, snapshot.Count);

                var evt = snapshot[0];
                Assert.AreEqual("POST", evt.Method);
                Assert.AreEqual("https://api.example.com/v1/items", evt.Url);
                Assert.AreEqual(200, evt.StatusCode);
                Assert.AreEqual(HttpMonitorFailureKind.None, evt.FailureKind);
                Assert.IsFalse(evt.IsError);
                Assert.That(evt.GetRequestBodyAsString(), Does.Contain("TurboHTTP"));
                Assert.That(evt.GetResponseBodyAsString(), Does.Contain("\"ok\":true"));
                Assert.That(evt.Timeline.Count, Is.EqualTo(1));
                Assert.That(evt.Timeline[0].Name, Is.EqualTo("CustomStage"));
                Assert.That(evt.Timeline[0].Data["attempt"], Is.EqualTo("1"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CapturesTransportFailure()
        {
            var middleware = new MonitorMiddleware();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
            });
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(
                () => pipeline.ExecuteAsync(request, context));
            Assert.That(ex.HttpError.Type, Is.EqualTo(UHttpErrorType.NetworkError));

            var snapshot = new List<HttpMonitorEvent>();
            MonitorMiddleware.GetHistorySnapshot(snapshot);
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(HttpMonitorFailureKind.TransportError, snapshot[0].FailureKind);
            Assert.AreEqual(UHttpErrorType.NetworkError, snapshot[0].ErrorType);
            Assert.That(snapshot[0].Error, Does.Contain("Connection refused"));
            Assert.AreEqual(0, snapshot[0].StatusCode);
        }

        [Test]
        public void ClassifiesHttpStatusErrorsSeparatelyFromTransportFailures()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.InternalServerError);
                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[] { new MonitorMiddleware() },
                    transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/error"));
                var context = new RequestContext(request);
                await pipeline.ExecuteAsync(request, context);

                var snapshot = new List<HttpMonitorEvent>();
                MonitorMiddleware.GetHistorySnapshot(snapshot);

                Assert.AreEqual(1, snapshot.Count);
                Assert.AreEqual(500, snapshot[0].StatusCode);
                Assert.AreEqual(HttpMonitorFailureKind.HttpStatusError, snapshot[0].FailureKind);
                Assert.IsFalse(snapshot[0].IsTransportFailure);
                Assert.That(snapshot[0].Error, Does.Contain("HTTP 500"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HistoryIsBoundedAndEvictsOldest()
        {
            Task.Run(async () =>
            {
                MonitorMiddleware.HistoryCapacity = 3;
                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[] { new MonitorMiddleware() },
                    new MockTransport());

                for (int i = 0; i < 5; i++)
                {
                    var request = new UHttpRequest(
                        HttpMethod.GET,
                        new Uri($"https://api.example.com/{i}"));
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                }

                var snapshot = new List<HttpMonitorEvent>();
                MonitorMiddleware.GetHistorySnapshot(snapshot);

                Assert.AreEqual(3, snapshot.Count);
                Assert.AreEqual("https://api.example.com/2", snapshot[0].Url);
                Assert.AreEqual("https://api.example.com/3", snapshot[1].Url);
                Assert.AreEqual("https://api.example.com/4", snapshot[2].Url);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AppliesTextAndBinaryCapturePolicies()
        {
            Task.Run(async () =>
            {
                MonitorMiddleware.MaxCaptureSizeBytes = 8;
                MonitorMiddleware.BinaryPreviewBytes = 4;

                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Content-Type", "text/plain");
                var requestBody = Encoding.UTF8.GetBytes("0123456789ABCDEF");

                var responseHeaders = new HttpHeaders();
                responseHeaders.Set("Content-Type", "application/octet-stream");
                var responseBody = new byte[] { 1, 2, 3, 0, 4, 5, 6, 7, 8, 9 };

                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    responseHeaders,
                    responseBody);
                var pipeline = new HttpPipeline(
                    new IHttpMiddleware[] { new MonitorMiddleware() },
                    transport);

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://files.example.com/upload"),
                    requestHeaders,
                    requestBody);

                await pipeline.ExecuteAsync(request, new RequestContext(request));

                var snapshot = new List<HttpMonitorEvent>();
                MonitorMiddleware.GetHistorySnapshot(snapshot);
                Assert.AreEqual(1, snapshot.Count);

                var evt = snapshot[0];
                Assert.AreEqual(16, evt.OriginalRequestBodySize);
                Assert.AreEqual(8, evt.RequestBody.Length);
                Assert.IsTrue(evt.IsRequestBodyTruncated);
                Assert.IsFalse(evt.IsRequestBodyBinary);
                Assert.That(evt.GetRequestBodyAsString(), Does.Contain("<Truncated: showing 8/16 bytes>"));

                Assert.AreEqual(10, evt.OriginalResponseBodySize);
                Assert.AreEqual(4, evt.ResponseBody.Length);
                Assert.IsTrue(evt.IsResponseBodyTruncated);
                Assert.IsTrue(evt.IsResponseBodyBinary);
                Assert.That(evt.GetResponseBodyAsString(), Does.Contain("<Binary Data: 10 bytes, preview only>"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ClearHistoryNotifiesListenersAndEmptiesBuffer()
        {
            Task.Run(async () =>
            {
                int clearNotificationCount = 0;
                Action<HttpMonitorEvent> handler = evt =>
                {
                    if (evt == null)
                    {
                        Interlocked.Increment(ref clearNotificationCount);
                    }
                };

                MonitorMiddleware.OnRequestCaptured += handler;
                try
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                    var pipeline = new HttpPipeline(
                        new IHttpMiddleware[] { new MonitorMiddleware() },
                        new MockTransport());
                    await pipeline.ExecuteAsync(request, new RequestContext(request));

                    Assert.AreEqual(1, MonitorMiddleware.HistoryCount);
                    MonitorMiddleware.ClearHistory();

                    Assert.AreEqual(0, MonitorMiddleware.HistoryCount);
                    Assert.AreEqual(1, clearNotificationCount);
                }
                finally
                {
                    MonitorMiddleware.OnRequestCaptured -= handler;
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ListenerFailuresDoNotBlockOtherSubscribers()
        {
            Task.Run(async () =>
            {
                var diagnostics = new List<string>();
                MonitorMiddleware.DiagnosticLogger = diagnostics.Add;

                int goodHandlerCount = 0;
                Action<HttpMonitorEvent> badHandler = _ => throw new InvalidOperationException("listener failed");
                Action<HttpMonitorEvent> goodHandler = _ => Interlocked.Increment(ref goodHandlerCount);

                MonitorMiddleware.OnRequestCaptured += badHandler;
                MonitorMiddleware.OnRequestCaptured += goodHandler;
                try
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                    var pipeline = new HttpPipeline(
                        new IHttpMiddleware[] { new MonitorMiddleware() },
                        new MockTransport());
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                }
                finally
                {
                    MonitorMiddleware.OnRequestCaptured -= badHandler;
                    MonitorMiddleware.OnRequestCaptured -= goodHandler;
                }

                Assert.AreEqual(1, goodHandlerCount);
                Assert.That(diagnostics.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(diagnostics[0], Does.Contain("Capture failure"));
            }).GetAwaiter().GetResult();
        }

        private static void ResetMonitor()
        {
            MonitorMiddleware.CaptureEnabled = true;
            MonitorMiddleware.HeaderValueTransform = null;
            MonitorMiddleware.DiagnosticLogger = null;
            MonitorMiddleware.HistoryCapacity = 1000;
            MonitorMiddleware.MaxCaptureSizeBytes = 5 * 1024 * 1024;
            MonitorMiddleware.BinaryPreviewBytes = 64 * 1024;
            MonitorMiddleware.ClearHistory();
        }
    }
}
