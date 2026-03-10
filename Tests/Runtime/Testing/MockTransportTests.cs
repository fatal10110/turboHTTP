using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Tests;

namespace TurboHTTP.Tests.Testing
{
    [TestFixture]
    public class MockTransportTests
    {
        [Test]
        public void DispatchAsync_QueuedResponse_CallsRequestStartBeforeResponseCallbacks()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK, body: Encoding.UTF8.GetBytes("ok"));

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/mock"));
                var context = new RequestContext(request);
                var handler = new CallbackRecorder();

                await transport.DispatchAsync(request, handler, context, CancellationToken.None);

                CollectionAssert.AreEqual(
                    new[] { "request", "start:200", "data:2", "end" },
                    handler.Events);
                Assert.AreEqual("ok", handler.GetBodyAsString());
                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("https://example.test/mock", transport.CapturedRequests[0].Uri.ToString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DispatchAsync_QueuedError_CallsRequestStartThenResponseError()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueError(
                    new UHttpError(UHttpErrorType.NetworkError, "boom"),
                    statusCode: HttpStatusCode.ServiceUnavailable);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/error"));
                var context = new RequestContext(request);
                var handler = new CallbackRecorder();

                await transport.DispatchAsync(request, handler, context, CancellationToken.None);

                CollectionAssert.AreEqual(new[] { "request", "error" }, handler.Events);
                Assert.IsNotNull(handler.Error);
                Assert.AreEqual(UHttpErrorType.NetworkError, handler.Error.HttpError.Type);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, handler.Error.HttpError.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DispatchAsync_DelayedResponse_HonorsCancellationAfterRequestStart()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(
                    HttpStatusCode.OK,
                    delay: TimeSpan.FromSeconds(5));

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/slow"));
                var context = new RequestContext(request);
                var handler = new CallbackRecorder();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await transport.DispatchAsync(request, handler, context, cts.Token);
                });

                CollectionAssert.AreEqual(new[] { "request" }, handler.Events);
                Assert.IsNull(handler.Error);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DispatchAsync_WithoutQueuedResponse_ReportsNetworkErrorAfterRequestStart()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/empty"));
                var context = new RequestContext(request);
                var handler = new CallbackRecorder();

                await transport.DispatchAsync(request, handler, context, CancellationToken.None);

                CollectionAssert.AreEqual(new[] { "request", "error" }, handler.Events);
                Assert.IsNotNull(handler.Error);
                Assert.AreEqual(UHttpErrorType.NetworkError, handler.Error.HttpError.Type);
                Assert.AreEqual("MockTransport: no queued response", handler.Error.HttpError.Message);
            }).GetAwaiter().GetResult();
        }

        private sealed class CallbackRecorder : IHttpHandler
        {
            private readonly List<byte> _body = new List<byte>();

            public List<string> Events { get; } = new List<string>();
            public UHttpException Error { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                Events.Add("request");
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                Events.Add("start:" + statusCode);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                Events.Add("data:" + chunk.Length);
                if (!chunk.IsEmpty)
                    _body.AddRange(chunk.ToArray());
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                Events.Add("end");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
                Events.Add("error");
            }

            public string GetBodyAsString()
            {
                return _body.Count == 0 ? null : Encoding.UTF8.GetString(_body.ToArray());
            }
        }
    }
}
