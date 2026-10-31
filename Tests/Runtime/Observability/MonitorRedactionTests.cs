using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    /// <summary>
    /// #92: Monitor redacts at capture time, captures only with a viewer (or opt-in), and bounds the
    /// history by a byte budget. #186 part 2: the monitor reaches UHttpClient only through EditorMonitorHook.
    /// </summary>
    public class MonitorRedactionTests
    {
        private const string Secret = "s3cr3t-value-123";

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
        public void DefaultCapture_RedactsCredentialHeadersAndQueryValues()
        {
            using (MonitorInterceptor.AttachViewer())
            {
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Authorization", "Bearer " + Secret);
                requestHeaders.Set("Proxy-Authorization", "Basic " + Secret);
                requestHeaders.Set("Cookie", "sid=" + Secret);
                requestHeaders.Set("Accept", "application/json");
                var responseHeaders = new HttpHeaders();
                responseHeaders.Add("Set-Cookie", "sid=" + Secret + "; Path=/");
                responseHeaders.Set("Content-Type", "text/plain");

                var evt = CaptureOne(
                    "https://api.example.com/items?access_token=" + Secret + "&page=2",
                    requestHeaders,
                    responseHeaders);

                Assert.AreEqual(RedactionPolicy.RedactedValue, evt.RequestHeaders["Authorization"]);
                Assert.AreEqual(RedactionPolicy.RedactedValue, evt.RequestHeaders["Proxy-Authorization"]);
                Assert.AreEqual(RedactionPolicy.RedactedValue, evt.RequestHeaders["Cookie"]);
                Assert.AreEqual(RedactionPolicy.RedactedValue, evt.ResponseHeaders["Set-Cookie"]);
                Assert.AreEqual("application/json", evt.RequestHeaders["Accept"]);
                Assert.AreEqual(
                    "https://api.example.com/items?access_token=[REDACTED]&page=[REDACTED]",
                    evt.Url);
                Assert.That(evt.Url, Does.Not.Contain(Secret));
            }
        }

        [Test]
        public void RedactionOptOut_KeepsRawValues()
        {
            MonitorInterceptor.RedactionPolicy = RedactionPolicy.None;
            using (MonitorInterceptor.AttachViewer())
            {
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Authorization", "Bearer " + Secret);

                var evt = CaptureOne("https://api.example.com/items?page=2", requestHeaders, null);

                Assert.AreEqual("Bearer " + Secret, evt.RequestHeaders["Authorization"]);
                Assert.AreEqual("https://api.example.com/items?page=2", evt.Url);
            }

            MonitorInterceptor.RedactionPolicy = null;
            Assert.AreSame(RedactionPolicy.Default, MonitorInterceptor.RedactionPolicy);
        }

        [Test]
        public void NoViewer_CapturesNothing()
        {
            Assume.That(MonitorInterceptor.ViewerCount, Is.EqualTo(0), "an HTTP Monitor window is open");
            Assert.IsTrue(MonitorInterceptor.CaptureEnabled);
            Assert.IsFalse(MonitorInterceptor.IsCapturing);

            Execute("https://api.example.com/closed", null, null);

            Assert.AreEqual(0, MonitorInterceptor.HistoryCount);
            Assert.AreEqual(0, MonitorInterceptor.TotalCapturedBytes);
        }

        [Test]
        public void CaptureWithoutViewer_OptInCaptures()
        {
            MonitorInterceptor.CaptureWithoutViewer = true;
            Assert.IsTrue(MonitorInterceptor.IsCapturing);

            Execute("https://api.example.com/opt-in", null, null);

            Assert.AreEqual(1, MonitorInterceptor.HistoryCount);
        }

        [Test]
        public void ViewerHandle_DetachesOnceAndStopsCapture()
        {
            Assume.That(MonitorInterceptor.ViewerCount, Is.EqualTo(0), "an HTTP Monitor window is open");
            var viewer = MonitorInterceptor.AttachViewer();
            Assert.AreEqual(1, MonitorInterceptor.ViewerCount);
            Assert.IsTrue(MonitorInterceptor.IsCapturing);

            viewer.Dispose();
            viewer.Dispose();

            Assert.AreEqual(0, MonitorInterceptor.ViewerCount);
            Assert.IsFalse(MonitorInterceptor.IsCapturing);

            MonitorInterceptor.CaptureEnabled = false;
            using (MonitorInterceptor.AttachViewer())
            {
                Assert.IsFalse(MonitorInterceptor.IsCapturing, "the master switch still wins");
            }
        }

        [Test]
        public void HistoryIsBoundedByTotalByteBudget()
        {
            const long budget = 64 * 1024;
            MonitorInterceptor.MaxTotalCaptureBytes = budget;
            MonitorInterceptor.CaptureWithoutViewer = true;

            for (int i = 0; i < 40; i++)
            {
                Execute("https://api.example.com/" + i, null, null, new byte[10 * 1024]);
                Assert.That(MonitorInterceptor.TotalCapturedBytes, Is.LessThanOrEqualTo(budget));
            }

            var snapshot = new List<HttpMonitorEvent>();
            MonitorInterceptor.GetHistorySnapshot(snapshot);
            Assert.That(snapshot.Count, Is.GreaterThan(0));
            Assert.That(snapshot.Count, Is.LessThan(40), "oldest entries were evicted");
            Assert.AreEqual("https://api.example.com/39", snapshot[snapshot.Count - 1].Url);
            Assert.That(MonitorInterceptor.TotalCapturedBytes, Is.GreaterThan(snapshot.Count * 10L * 1024));

            MonitorInterceptor.ClearHistory();
            Assert.AreEqual(0, MonitorInterceptor.TotalCapturedBytes);
        }

        [Test]
        public void BodyIsTruncatedToAQuarterOfTheBudget()
        {
            const long budget = 64 * 1024;
            MonitorInterceptor.MaxTotalCaptureBytes = budget;
            MonitorInterceptor.CaptureWithoutViewer = true;
            var body = Encoding.ASCII.GetBytes(new string('a', 40 * 1024));

            Execute("https://api.example.com/big", null, TextHeaders(), body);

            var snapshot = new List<HttpMonitorEvent>();
            MonitorInterceptor.GetHistorySnapshot(snapshot);
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(budget / 4, snapshot[0].ResponseBody.Length);
            Assert.IsTrue(snapshot[0].IsResponseBodyTruncated);
            Assert.AreEqual(body.Length, snapshot[0].OriginalResponseBodySize);
        }

        [Test]
        public void LoweringTheBudgetEvictsImmediately()
        {
            MonitorInterceptor.CaptureWithoutViewer = true;
            for (int i = 0; i < 10; i++)
                Execute("https://api.example.com/" + i, null, TextHeaders(), new byte[8 * 1024]);
            Assert.AreEqual(10, MonitorInterceptor.HistoryCount);

            MonitorInterceptor.MaxTotalCaptureBytes = 20 * 1024;

            Assert.That(MonitorInterceptor.TotalCapturedBytes, Is.LessThanOrEqualTo(20 * 1024));
            Assert.That(MonitorInterceptor.HistoryCount, Is.LessThan(10));
        }

        [Test]
        public void Hook_AbsentRegistration_IsNoOp()
        {
            EditorMonitorHook.Register(null);
            var interceptors = new List<IHttpInterceptor>();

            Assert.IsFalse(EditorMonitorHook.IsRegistered);
            Assert.IsFalse(EditorMonitorHook.TryAppend(interceptors));
            Assert.IsEmpty(interceptors);
        }

        [Test]
        public void Hook_ObservabilityRegistration_AppendsMonitorOnce()
        {
            ObservabilityModuleRegistration.EnsureRegistered();
            Assert.IsTrue(EditorMonitorHook.IsRegistered);

            var interceptors = new List<IHttpInterceptor>();
            Assert.IsTrue(EditorMonitorHook.TryAppend(interceptors));
            Assert.AreEqual(1, interceptors.Count);
            Assert.IsInstanceOf<MonitorInterceptor>(interceptors[0]);

            Assert.IsFalse(EditorMonitorHook.TryAppend(interceptors), "a configured monitor is not duplicated");
            Assert.AreEqual(1, interceptors.Count);

            MonitorInterceptor.CaptureEnabled = false;
            var disabled = new List<IHttpInterceptor>();
            Assert.IsFalse(EditorMonitorHook.TryAppend(disabled));
            Assert.IsEmpty(disabled);
        }

        [Test]
        public void Hook_ThrowingFactory_DoesNotBreakConstruction()
        {
            EditorMonitorHook.Register(_ => throw new InvalidOperationException("boom"));
            var interceptors = new List<IHttpInterceptor>();

            Assert.IsFalse(EditorMonitorHook.TryAppend(interceptors));
            Assert.IsEmpty(interceptors);
        }

        private static HttpHeaders TextHeaders()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "text/plain");
            return headers;
        }

        private static HttpMonitorEvent CaptureOne(string url, HttpHeaders requestHeaders, HttpHeaders responseHeaders)
        {
            Execute(url, requestHeaders, responseHeaders);
            var snapshot = new List<HttpMonitorEvent>();
            MonitorInterceptor.GetHistorySnapshot(snapshot);
            Assert.AreEqual(1, snapshot.Count);
            return snapshot[0];
        }

        private static void Execute(
            string url,
            HttpHeaders requestHeaders,
            HttpHeaders responseHeaders,
            byte[] responseBody = null)
        {
            var transport = new MockTransport();
            transport.EnqueueResponse(HttpStatusCode.OK, responseHeaders, responseBody);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(url), requestHeaders);
            AssertAsync.Run(async () =>
            {
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new MonitorInterceptor() },
                    transport);
                await pipeline.ExecuteAsync(request, new RequestContext(request));
            });
        }

        private static void Reset()
        {
            MonitorInterceptor.CaptureEnabled = true;
            MonitorInterceptor.CaptureWithoutViewer = false;
            MonitorInterceptor.RedactionPolicy = null;
            MonitorInterceptor.HeaderValueTransform = null;
            MonitorInterceptor.DiagnosticLogger = null;
            MonitorInterceptor.HistoryCapacity = 1000;
            MonitorInterceptor.MaxCaptureSizeBytes = 5 * 1024 * 1024;
            MonitorInterceptor.BinaryPreviewBytes = 64 * 1024;
            MonitorInterceptor.MaxTotalCaptureBytes = 32L * 1024 * 1024;
            MonitorInterceptor.ClearHistory();
        }
    }
}
