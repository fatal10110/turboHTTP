using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Stream lifetime (RFC 9113 Sections 5.1, 8.1, 8.1.1): the upload continues after the response
    /// starts or completes and always ends with END_STREAM or RST_STREAM (#19), early finals, interim
    /// and malformed responses reset the stream (#28), content-length is checked against the DATA total
    /// (#25), the read loop never touches a recycled stream (#21), and streamed bodies have no size cap
    /// (#147).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        private const int LifecycleUploadBytes = 200 * 1024;

        // --- #19: the upload continues after the response starts ---

        [Test]
        public void StreamLifecycle_StreamingResponseHeadersFirst_UploadStillCompletesWithEndStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"))
                    .WithBody(new byte[LifecycleUploadBytes]);
                var responseTask = conn.SendStreamingRequestAsync(request, new RequestContext(request), cts.Token)
                    .AsTask();

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1: the server answers at once and only then reads the whole body.
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 200), cts.Token);
                int uploaded = await ReadUploadGrantingWindowAsync(serverCodec, headers.StreamId, cts.Token);
                Assert.AreEqual(LifecycleUploadBytes, uploaded, "the whole body arrives, ending with END_STREAM");

                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "done", endStream: true), cts.Token);
                await using var response = await WithWatchdogResult(responseTask, cts.Token);
                Assert.AreEqual("done", await ReadAllTextAsync(response, cts.Token));
                Assert.AreEqual(0, GetActiveStreams(conn).Count, "the stream closed in both directions");
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void StreamLifecycle_CompleteEarlyResponse_UploadContinuesUntilBodyEnds()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/early"))
                    .WithBody(new byte[LifecycleUploadBytes]);
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 202, endStream: true), cts.Token);

                // A complete response is not a reason to stop: only RST_STREAM is (RFC 9113 Section 8.1).
                int uploaded = await ReadUploadGrantingWindowAsync(serverCodec, headers.StreamId, cts.Token);
                Assert.AreEqual(LifecycleUploadBytes, uploaded);

                var response = await WithWatchdogResult(responseTask, cts.Token);
                Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(100));
                Assert.AreEqual(0, GetActiveStreams(conn).Count, "the stream closed in both directions");
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void StreamLifecycle_BodyAbandonedDuringBlockedUpload_EndsStreamWithOneRstCancel()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/abandon"))
                    .WithBody(new byte[4096]);
                var handler = new RecordingHandler(); // aborts the body at response start
                var dispatch = conn.DispatchAsync(request, handler, new RequestContext(request), cts.Token);

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 200), cts.Token);

                var reset = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, cts.Token);
                Assert.AreEqual(headers.StreamId, reset.StreamId);
                Assert.AreEqual((byte)Http2ErrorCode.Cancel, reset.Payload[3]);

                await WithWatchdog(dispatch, cts.Token);
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(150));
                Assert.AreEqual(0, GetActiveStreams(conn).Count);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- #28: early final, interim and malformed responses ---

        [Test]
        public void StreamLifecycle_ExpectContinueEarlyFinalWithBody_ResetsNoErrorOnlyAfterResponseEnds()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(
                    cts.Token,
                    streamingOptions: new StreamingOptions { ExpectContinueTimeoutMs = 5000 });
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                        HttpMethod.POST,
                        new Uri("https://test.example.com/expect"),
                        body: Encoding.UTF8.GetBytes("request body"))
                    .WithExpectContinue();
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                Assert.IsFalse(headers.HasFlag(Http2FrameFlags.EndStream));

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 417), cts.Token);

                // No DATA (the body is abandoned) and no reset yet: a reset would cut the response body.
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(150));

                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "nope", endStream: true), cts.Token);
                var response = await WithWatchdogResult(responseTask, cts.Token);
                Assert.AreEqual((HttpStatusCode)417, response.StatusCode);
                Assert.AreEqual("nope", response.GetBodyAsString());

                var reset = await ReadFrameMatchingAsync(serverCodec, f => f.Type != Http2FrameType.WindowUpdate, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, reset.Type);
                Assert.AreEqual(headers.StreamId, reset.StreamId);
                Assert.AreEqual((byte)Http2ErrorCode.NoError, reset.Payload[3]);
                Assert.AreEqual(0, GetActiveStreams(conn).Count);
                conn.Dispose();
            });
        }

        [Test]
        public void StreamLifecycle_InterimResponseWithEndStream_FailsPromptlyWithRstProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/interim"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1: an interim (1xx) HEADERS frame with END_STREAM is malformed.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 103, endStream: true), cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdogResult(responseTask, cts.Token));
                StringAssert.Contains("Malformed HTTP/2 response", ex.HttpError.Message);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void StreamLifecycle_ResponseWithoutStatus_ResetsProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/no-status"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                var block = new HpackEncoder()
                    .Encode(new List<(string, string)> { ("content-type", "text/plain") })
                    .ToArray();
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = headers.StreamId,
                    Payload = block,
                    Length = block.Length
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdogResult(responseTask, cts.Token));
                StringAssert.Contains(":status", ex.HttpError.Message);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- #25: content-length vs DATA total ---

        [Test]
        public void StreamLifecycle_ShortBodyAgainstContentLength_FaultsBufferedResponse()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/short"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(
                    headers.StreamId, 200, new Dictionary<string, string> { { "content-length", "10" } }), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "abcd", endStream: true), cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdogResult(responseTask, cts.Token));
                StringAssert.Contains("content-length", ex.HttpError.Message);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void StreamLifecycle_LongBodyAgainstContentLength_FaultsStreamingBody()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/long"));
                var responseTask = conn.SendStreamingRequestAsync(request, new RequestContext(request), cts.Token)
                    .AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(
                    headers.StreamId, 200, new Dictionary<string, string> { { "content-length", "4" } }), cts.Token);
                await using var response = await WithWatchdogResult(responseTask, cts.Token);

                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "abcdef", endStream: true), cts.Token);

                await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await ReadAllTextAsync(response, cts.Token));
                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.IsTrue(conn.IsAlive);
            });
        }

        [Test]
        public void StreamLifecycle_EndStreamHeadersWithContentLength_IsMalformed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/no-data"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1.1: content-length 10 with no DATA at all (GET 200 has content).
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(
                    headers.StreamId,
                    200,
                    new Dictionary<string, string> { { "content-length", "10" } },
                    endStream: true), cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdogResult(responseTask, cts.Token));
                StringAssert.Contains("content-length", ex.HttpError.Message);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.AreEqual(0, GetActiveStreams(conn).Count);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [TestCase("abcd")]
        [TestCase("abcdefghijkl")]
        public void StreamLifecycle_TrailersEndingBodyThatMismatchesContentLength_AreMalformed(string body)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/trailers"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1.1: the DATA total must equal content-length when trailers end the
                // stream too (a shorter body is caught at the trailers, a longer one at the DATA frame).
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(
                    headers.StreamId, 200, new Dictionary<string, string> { { "content-length", "10" } }), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, body, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildTrailingHeadersFrame(
                    headers.StreamId, new Dictionary<string, string> { { "x-checksum", "1" } }), cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdogResult(responseTask, cts.Token));
                StringAssert.Contains("content-length", ex.HttpError.Message);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.ProtocolError, cts.Token);
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(100));
                Assert.AreEqual(0, GetActiveStreams(conn).Count);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- RFC 9113 Section 5.1: frames on a half-closed (remote) stream while the upload runs ---

        [TestCase(false)]
        [TestCase(true)]
        public void StreamLifecycle_FrameAfterEndStreamDuringUpload_ResetsStreamClosed(bool lateHeaders)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // A zero stream window blocks the upload, so the stream stays active (half-closed remote)
                // after the response's END_STREAM.
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/half-closed"))
                    .WithBody(new byte[4096]);
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 200), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "ok", endStream: true), cts.Token);
                Assert.AreEqual(1, GetActiveStreams(conn).Count, "the upload keeps the stream open");

                await serverCodec.WriteFrameAsync(
                    lateHeaders
                        ? BuildTrailingHeadersFrame(headers.StreamId, new Dictionary<string, string> { { "x-late", "1" } })
                        : BuildDataFrame(headers.StreamId, "late", endStream: false),
                    cts.Token);

                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.StreamClosed, cts.Token);

                // The response was complete before the stream error and stands; the blocked upload stops
                // without a second RST_STREAM or more DATA.
                var response = await WithWatchdogResult(responseTask, cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok", response.GetBodyAsString());
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(100));
                Assert.AreEqual(0, GetActiveStreams(conn).Count);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [TestCase("HEAD", 200)]
        [TestCase("GET", 204)]
        [TestCase("GET", 304)]
        public void StreamLifecycle_NoContentResponses_AreExemptFromContentLengthCheck(string method, int status)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                    (HttpMethod)Enum.Parse(typeof(HttpMethod), method),
                    new Uri("https://test.example.com/no-content"));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1.1 / RFC 9110 Section 6.4.1: no content by definition.
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(
                    headers.StreamId,
                    status,
                    new Dictionary<string, string> { { "content-length", "10" } },
                    endStream: true), cts.Token);

                var response = await WithWatchdogResult(responseTask, cts.Token);
                Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
                await AssertNoFrameWithinAsync(serverCodec, TimeSpan.FromMilliseconds(100));
                conn.Dispose();
            });
        }

        // --- #21: the read loop never touches a recycled stream ---

        [Test]
        public void StreamLifecycle_Lease_KeepsStreamOutOfPool_AndRejectsRecycledIdentity()
        {
            var duplex = new TestDuplexStream();
            using var conn = new Http2Connection(
                duplex.ClientStream, "test.example.com", 443, new Http2Options(), new StreamingOptions());
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/lease"));
            var stream = Http2StreamPool.Rent(
                5, request, NoOpHttpHandler.Instance, new RequestContext(request), 65535, 65535, conn);

            Assert.IsTrue(stream.TryAcquireLease(5, conn));
            Assert.IsFalse(stream.TryAcquireLease(7, conn), "another stream id is another stream");

            // The owner lets go while the lease is held: the object must not be reset or pooled.
            stream.ReleaseDispatchLifetime();
            Assert.AreEqual(5, stream.StreamId);
            Assert.AreSame(request, stream.Request);

            stream.ReleaseLease();
            Assert.AreEqual(0, stream.StreamId, "the last reference returned the stream to the pool");
            Assert.IsFalse(stream.TryAcquireLease(5, conn), "a pooled stream cannot be leased");

            // Re-rented for another stream (the pool is LIFO, so usually the same object): a stale
            // lookup by the old id, or from another connection, is rejected either way.
            var rerented = Http2StreamPool.Rent(
                9, request, NoOpHttpHandler.Instance, new RequestContext(request), 65535, 65535, conn);
            Assert.IsFalse(stream.TryAcquireLease(5, conn));
            Assert.IsFalse(rerented.TryAcquireLease(9, null));
            Assert.IsTrue(rerented.TryAcquireLease(9, conn));
            rerented.ReleaseLease();
            Assert.AreEqual(9, rerented.StreamId, "a released lease leaves the owner's reference");
            rerented.ReleaseDispatchLifetime();
        }

        [Test]
        public void StreamLifecycle_Stress_CancellationsDuringDelivery_NoCrossDeliveryAndConnectionsSurvive()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(60000);
                var first = RunCancellationStressAsync(cts.Token);
                var second = RunCancellationStressAsync(cts.Token);
                await WithWatchdog(Task.WhenAll(first, second), cts.Token);
            });
        }

        // --- #147: streamed bodies have no size cap ---

        [Test]
        public void StreamLifecycle_StreamingResponseMoreThan100MB_ReadsToEndWithDefaultOptions()
        {
            AssertAsync.Run(async () =>
            {
                const long total = (100L * 1024 * 1024) + 1;
                using var cts = new CancellationTokenSource(180000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/huge"));
                var responseTask = conn.SendStreamingRequestAsync(request, new RequestContext(request), cts.Token)
                    .AsTask();
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 200), cts.Token);
                await using var response = await WithWatchdogResult(responseTask, cts.Token);

                var serverTask = Task.Run(
                    () => SendBodyHonoringFlowControlAsync(serverCodec, headers.StreamId, total, cts.Token),
                    cts.Token);

                var buffer = new byte[64 * 1024];
                long received = 0;
                while (true)
                {
                    int read = await response.Body.ReadAsync(buffer, cts.Token);
                    if (read == 0)
                        break;
                    received += read;
                }

                await WithWatchdog(serverTask, cts.Token);
                Assert.AreEqual(total, received);
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- helpers ---

        private async Task RunCancellationStressAsync(CancellationToken ct)
        {
            const int requestCount = 150;
            const int batchSize = 25;
            var (conn, serverStream, _) = await CreateInitializedConnectionAsync(ct);
            var serverCodec = new Http2FrameCodec(serverStream);
            var serverTask = Task.Run(() => EchoPathServerAsync(serverCodec, ct), ct);

            try
            {
                for (int start = 0; start < requestCount; start += batchSize)
                {
                    var tasks = new List<Task<(string Path, string Body)>>();
                    var sources = new List<CancellationTokenSource>();
                    for (int i = start; i < start + batchSize; i++)
                    {
                        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sources.Add(requestCts);
                        tasks.Add(SendEchoRequestAsync(conn, "/r" + i, i, requestCts));
                    }

                    foreach (var task in tasks)
                    {
                        try
                        {
                            var (path, body) = await task;
                            Assert.AreEqual(path, body, "a response must never reach another request");
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Expected for cancelled requests.
                        }
                    }

                    foreach (var source in sources)
                        source.Dispose();

                    Assert.IsTrue(conn.IsAlive, "cancellations must never fail the connection");
                }

                var (finalPath, finalBody) = await SendEchoRequestAsync(
                    conn, "/final", 2, CancellationTokenSource.CreateLinkedTokenSource(ct));
                Assert.AreEqual(finalPath, finalBody);
                await serverTask;
                Assert.IsTrue(conn.IsAlive);
            }
            finally
            {
                conn.Dispose();
            }
        }

        private static async Task<(string Path, string Body)> SendEchoRequestAsync(
            Http2Connection conn, string path, int index, CancellationTokenSource requestCts)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com" + path));
            var task = conn.SendRequestAsync(request, new RequestContext(request), requestCts.Token).AsTask();

            switch (index % 3)
            {
                case 0:
                    requestCts.Cancel();
                    break;
                case 1:
                    for (int y = 0; y < index % 7; y++)
                        await Task.Yield();
                    requestCts.Cancel();
                    break;
            }

            using var response = await task;
            return (path, response.GetBodyAsString());
        }

        /// <summary>Answers every request with HEADERS and one DATA frame carrying its :path.</summary>
        private async Task EchoPathServerAsync(Http2FrameCodec serverCodec, CancellationToken ct)
        {
            var decoder = new HpackDecoder();
            while (true)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.GoAway)
                    Assert.Fail("unexpected GOAWAY from the client");
                if (frame.Type != Http2FrameType.Headers)
                    continue;

                var path = decoder.Decode(frame.Payload, 0, frame.Length).First(h => h.Name == ":path").Value;
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(frame.StreamId, 200), ct);
                await serverCodec.WriteFrameAsync(BuildDataFrame(frame.StreamId, path, endStream: true), ct);
                if (path == "/final")
                    return;
            }
        }

        /// <summary>
        /// Reads the stream's DATA until END_STREAM, returning stream and connection credit for every
        /// frame so the uploader can continue past the initial window.
        /// </summary>
        private static async Task<int> ReadUploadGrantingWindowAsync(
            Http2FrameCodec serverCodec, int streamId, CancellationToken ct)
        {
            int received = 0;
            while (true)
            {
                var frame = await ReadFrameMatchingAsync(
                    serverCodec, f => f.Type == Http2FrameType.Data || f.Type == Http2FrameType.RstStream, ct);
                Assert.AreEqual(Http2FrameType.Data, frame.Type, "the upload must not be reset");
                Assert.AreEqual(streamId, frame.StreamId);
                received += frame.Length;
                if (frame.HasFlag(Http2FrameFlags.EndStream))
                    return received;

                if (frame.Length > 0)
                {
                    await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(0, frame.Length), ct);
                    await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(streamId, frame.Length), ct);
                }
            }
        }

        /// <summary>Sends <paramref name="total"/> zero bytes in DATA frames within the peer's windows.</summary>
        private static async Task SendBodyHonoringFlowControlAsync(
            Http2FrameCodec serverCodec, int streamId, long total, CancellationToken ct)
        {
            var payload = new byte[16384];
            long connectionWindow = 65535;
            long streamWindow = 65535;
            long remaining = total;
            while (remaining > 0)
            {
                int length = (int)Math.Min(Math.Min(payload.Length, remaining), Math.Min(connectionWindow, streamWindow));
                if (length <= 0)
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, ct);
                    if (frame.Type == Http2FrameType.WindowUpdate)
                    {
                        int increment = ReadWindowIncrement(frame);
                        if (frame.StreamId == 0)
                            connectionWindow += increment;
                        else if (frame.StreamId == streamId)
                            streamWindow += increment;
                    }
                    else if (frame.Type == Http2FrameType.RstStream || frame.Type == Http2FrameType.GoAway)
                    {
                        Assert.Fail($"unexpected {frame.Type} while streaming a large body");
                    }

                    continue;
                }

                remaining -= length;
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Data,
                    Flags = remaining == 0 ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = length
                }, ct);
                connectionWindow -= length;
                streamWindow -= length;
            }
        }

        private static async Task AssertResetAsync(
            Http2FrameCodec serverCodec, int streamId, Http2ErrorCode code, CancellationToken ct)
        {
            var reset = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, ct);
            Assert.AreEqual(streamId, reset.StreamId);
            Assert.AreEqual(4, reset.Length);
            Assert.AreEqual((byte)code, reset.Payload[3]);
        }

        private static Http2Frame BuildDataFrame(int streamId, string text, bool endStream)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            return new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                StreamId = streamId,
                Payload = payload,
                Length = payload.Length
            };
        }

        private static async Task<string> ReadAllTextAsync(UHttpStreamingResponse response, CancellationToken ct)
        {
            var buffer = new byte[1024];
            var text = new StringBuilder();
            while (true)
            {
                int read = await response.Body.ReadAsync(buffer, ct);
                if (read == 0)
                    return text.ToString();
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
    }
}
