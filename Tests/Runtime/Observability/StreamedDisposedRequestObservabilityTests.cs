using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Observability
{
    /// <summary>
    /// A client-created request is disposed once a streamed response's headers arrive, before its body
    /// is disposed. Metrics and Monitor complete in the body-dispose callback, so they must capture the
    /// request data they need while it is live, and an observer callback must never make the caller's
    /// dispose throw (#1053).
    /// </summary>
    [TestFixture]
    public class StreamedDisposedRequestObservabilityTests
    {
        private const string Url = "https://example.test/stream";
        private static readonly byte[] ResponsePayload = Encoding.UTF8.GetBytes("payload");

        private bool _savedCaptureEnabled;
        private bool _savedCaptureWithoutViewer;

        [SetUp]
        public void SetUp()
        {
            _savedCaptureEnabled = MonitorInterceptor.CaptureEnabled;
            _savedCaptureWithoutViewer = MonitorInterceptor.CaptureWithoutViewer;
            MonitorInterceptor.CaptureEnabled = true;
            MonitorInterceptor.CaptureWithoutViewer = true;
            MonitorInterceptor.RedactionPolicy = null;
            MonitorInterceptor.ClearHistory();
        }

        [TearDown]
        public void TearDown()
        {
            MonitorInterceptor.ClearHistory();
            MonitorInterceptor.RedactionPolicy = null;
            MonitorInterceptor.CaptureEnabled = _savedCaptureEnabled;
            MonitorInterceptor.CaptureWithoutViewer = _savedCaptureWithoutViewer;
        }

        [Test]
        public void StreamedGet_FromClientRequest_RecordsMetricsAndMonitorEventOnDispose()
        {
            AssertAsync.Run(async () =>
            {
                var metrics = new MetricsInterceptor();
                using var client = CreateClient(metrics, new MonitorInterceptor());

                var response = await client.Get(Url).SendStreamingAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                await DrainAsync(response);
                Assert.AreEqual(0, metrics.Metrics.SuccessfulRequests, "completion must wait for the body dispose");

                await response.DisposeAsync();

                Assert.AreEqual(1, metrics.Metrics.SuccessfulRequests);
                Assert.AreEqual(0, metrics.Metrics.FailedRequests);
                Assert.AreEqual(ResponsePayload.Length, metrics.Metrics.TotalBytesReceived);

                var monitorEvent = SingleMonitorEvent();
                Assert.AreEqual("GET", monitorEvent.Method);
                Assert.AreEqual(Url, monitorEvent.Url);
                Assert.AreEqual(200, monitorEvent.StatusCode);
                Assert.AreEqual("payload", monitorEvent.GetResponseBodyAsString());
            });
        }

        [Test]
        public void StreamedPost_WithBufferedBody_SnapshotsLengthAndBodyBeforeRequestDispose()
        {
            AssertAsync.Run(async () =>
            {
                var metrics = new MetricsInterceptor();
                using var client = CreateClient(metrics, new MonitorInterceptor());

                var response = await client.Post(Url)
                    .WithHeader("X-Probe", "probe-value")
                    .WithBody("hello")
                    .SendStreamingAsync();
                await DrainAsync(response);
                await response.DisposeAsync();

                Assert.AreEqual(1, metrics.Metrics.SuccessfulRequests);
                // The transport reports no RequestBodyBytesSent, so the content length snapshot is used.
                Assert.AreEqual(5, metrics.Metrics.TotalBytesSent);

                var monitorEvent = SingleMonitorEvent();
                Assert.AreEqual("POST", monitorEvent.Method);
                Assert.AreEqual("hello", Encoding.UTF8.GetString(monitorEvent.RequestBody.ToArray()));
                Assert.AreEqual(string.Empty, monitorEvent.RequestBodyCaptureNote);
                Assert.IsTrue(monitorEvent.RequestHeaders.TryGetValue("X-Probe", out var probe));
                Assert.AreEqual("probe-value", probe);
            });
        }

        [Test]
        public void StreamedPost_WithStreamedBody_SnapshotsCaptureNoteBeforeRequestDispose()
        {
            AssertAsync.Run(async () =>
            {
                var metrics = new MetricsInterceptor();
                using var client = CreateClient(metrics, new MonitorInterceptor());

                var response = await client.Post(Url)
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(new byte[] { 1, 2, 3 })),
                        contentLength: 3)
                    .SendStreamingAsync();
                await DrainAsync(response);
                await response.DisposeAsync();

                Assert.AreEqual(1, metrics.Metrics.SuccessfulRequests);
                Assert.AreEqual(3, metrics.Metrics.TotalBytesSent);

                var monitorEvent = SingleMonitorEvent();
                Assert.AreEqual(0, monitorEvent.RequestBody.Length);
                Assert.That(monitorEvent.RequestBodyCaptureNote, Does.Contain("length=3"));
            });
        }

        [Test]
        public void ObservedBody_ThrowingDisposeCallback_DoesNotEscapeDisposeAsync()
        {
            AssertAsync.Run(async () =>
            {
                var callbacks = 0;
                var observed = new ObservedResponseBodySource(
                    NewStreamedBody(),
                    onChunkRead: null,
                    onDispose: _ =>
                    {
                        callbacks++;
                        throw new InvalidOperationException("observer failure");
                    });

                await observed.DisposeAsync();

                Assert.AreEqual(1, callbacks);
            });
        }

        private static async Task DrainAsync(UHttpStreamingResponse response)
        {
            var buffer = new byte[64];
            while (await response.Body.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None) > 0)
            {
            }
        }

        private static HttpMonitorEvent SingleMonitorEvent()
        {
            var history = new List<HttpMonitorEvent>();
            MonitorInterceptor.GetHistorySnapshot(history);
            Assert.AreEqual(1, history.Count, "exactly one Monitor event must be recorded");
            return history[0];
        }

        private static MockResponseBodySource NewStreamedBody()
        {
            // No buffered data, so Metrics/Monitor wrap it and complete in the dispose callback.
            return new MockResponseBodySource(
                new[] { (ReadOnlyMemory<byte>)ResponsePayload },
                length: ResponsePayload.Length,
                trailers: HttpHeaders.Empty,
                exposeBufferedData: false);
        }

        private static UHttpClient CreateClient(params IHttpInterceptor[] interceptors)
        {
            var options = new UHttpClientOptions
            {
                Transport = new StreamingTransport(),
                FollowRedirects = false
            };
            options.Interceptors.AddRange(interceptors);
            return new UHttpClient(options);
        }

        private sealed class StreamingTransport : IHttpTransport
        {
            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync(
                    (int)HttpStatusCode.OK,
                    new HttpHeaders(),
                    NewStreamedBody(),
                    context).AsTask();
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
