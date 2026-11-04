using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Deterministic short variants of the soak harness's HTTP/2 leak oracle (#114, Tools~/soak): every
    /// stream outcome a lossy network produces (completed, reset by the peer, abandoned mid-body,
    /// cancelled while waiting for headers, cancelled while blocked on a zero flow-control window,
    /// refused by GOAWAY) hands its pooled <see cref="Http2Stream"/> back and leaves the active map empty.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        private static readonly byte[] PoolLeakPingPayload = { 1, 1, 4, 0, 0, 0, 0, 0 };

        [Test]
        public void PoolLeak_MixedOutcomesUnderZeroWindow_EveryStreamReturnsToPool()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // RFC 9113 Section 6.9.2: every new stream starts with a zero send window (a stalled peer).
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                var completed = PoolLeakGetAsync(conn, "/completed", cts.Token);
                var completedHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var reset = PoolLeakGetAsync(conn, "/reset", cts.Token);
                var resetHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var abandonedRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/abandoned"));
                var abandoned = conn.SendStreamingRequestAsync(
                    abandonedRequest, new RequestContext(abandonedRequest), cts.Token).AsTask();
                var abandonedHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                using var cancelHeadersCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var cancelledWaitingHeaders = PoolLeakGetAsync(conn, "/cancel-headers", cancelHeadersCts.Token);
                var cancelledHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                using var cancelUploadCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var cancelledUpload = SendPostAsync(conn, "/cancel-upload", 4096, cancelUploadCts.Token);
                var cancelledUploadHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var creditedUpload = SendPostAsync(conn, "/credited-upload", 4096, cts.Token);
                var creditedUploadHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var streams = PoolLeakCaptureStreams(conn, 6);

                // Completed.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(completedHeaders.StreamId, 200, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(completedHeaders.StreamId, "ok", endStream: true), cts.Token);
                using (var response = await WithWatchdogResult(completed, cts.Token))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                // Reset by the peer before any response (RFC 9113 Section 6.4).
                await serverCodec.WriteFrameAsync(
                    BuildRstStreamFrame(resetHeaders.StreamId, Http2ErrorCode.InternalError), cts.Token);
                await PoolLeakAssertFailsAsync(reset, cts.Token);

                // Abandoned mid-body: the caller disposes a streamed response that is still open.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(abandonedHeaders.StreamId, 200, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(abandonedHeaders.StreamId, "partial", endStream: false), cts.Token);
                var streamed = await WithWatchdogResult(abandoned, cts.Token);
                var first = new byte[7];
                int read = 0;
                while (read < first.Length)
                {
                    int n = await streamed.Body.ReadAsync(first, read, first.Length - read, cts.Token);
                    Assert.Greater(n, 0, "the first DATA frame must reach the body stream");
                    read += n;
                }

                // DisposeAsync completes once the body source has sent RST_STREAM and released the stream.
                await streamed.DisposeAsync();
                await ReadFrameMatchingAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == abandonedHeaders.StreamId, cts.Token);

                // Cancelled while waiting for response headers.
                cancelHeadersCts.Cancel();
                await AssertCancelledAsync(cancelledWaitingHeaders, cts.Token);
                await ReadFrameMatchingAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == cancelledHeaders.StreamId, cts.Token);

                // Cancelled while blocked on the zero stream window (the peer never credits it).
                cancelUploadCts.Cancel();
                await AssertCancelledAsync(cancelledUpload, cts.Token);
                await ReadFrameMatchingAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == cancelledUploadHeaders.StreamId, cts.Token);

                // Credited late: the stalled upload completes once WINDOW_UPDATE arrives (RFC 9113 Section 6.9.1).
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(creditedUploadHeaders.StreamId, 4096), cts.Token);
                var uploaded = await ReadUploadsToEndAsync(serverCodec, new[] { creditedUploadHeaders.StreamId }, cts.Token);
                Assert.AreEqual(4096, uploaded[creditedUploadHeaders.StreamId]);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(creditedUploadHeaders.StreamId, 200, endStream: true), cts.Token);
                using (var response = await WithWatchdogResult(creditedUpload, cts.Token))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                await PoolLeakPingBarrierAsync(serverCodec, cts.Token);

                Assert.IsTrue(conn.IsAlive, "stream-level failures must not fail the connection");
                Assert.AreEqual(0, GetActiveStreams(conn).Count, "every stream left the active map");
                PoolLeakAssertReleased(conn, streams, "after six stream outcomes");

                conn.Dispose();
            });
        }

        [Test]
        public void PoolLeak_GoAwayRefusesInFlightStreams_DrainedStreamReturnsToPool()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var drainedRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/drained"));
                var drained = conn.SendStreamingRequestAsync(
                    drainedRequest, new RequestContext(drainedRequest), cts.Token).AsTask();
                var drainedHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var refusedA = PoolLeakGetAsync(conn, "/refused-a", cts.Token);
                await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var refusedB = PoolLeakGetAsync(conn, "/refused-b", cts.Token);
                await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var streams = PoolLeakCaptureStreams(conn, 3);

                // RFC 9113 Section 6.8: streams above the last stream id were not processed.
                await serverCodec.WriteFrameAsync(BuildGoAwayFrameForTest(drainedHeaders.StreamId, Http2ErrorCode.NoError), cts.Token);
                await PoolLeakAssertFailsAsync(refusedA, cts.Token);
                await PoolLeakAssertFailsAsync(refusedB, cts.Token);

                // The stream at the last stream id keeps running, and is abandoned mid-body.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(drainedHeaders.StreamId, 200, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(drainedHeaders.StreamId, "abc", endStream: false), cts.Token);
                var streamed = await WithWatchdogResult(drained, cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, streamed.StatusCode);
                await PoolLeakPingBarrierAsync(serverCodec, cts.Token);

                // DisposeAsync completes once the body source has sent RST_STREAM and released the stream.
                await streamed.DisposeAsync();

                Assert.AreEqual(0, GetActiveStreams(conn).Count, "the drained connection holds no stream");
                PoolLeakAssertReleased(conn, streams, "after GOAWAY and the drained stream");

                conn.Dispose();
            });
        }

        [Test]
        public void AbandonedStreamedBodies_FaultedTrailersAreObserved_NoUnobservedTaskException()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var unobserved = new ConcurrentQueue<Exception>();
                EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
                {
                    foreach (var inner in e.Exception.InnerExceptions)
                    {
                        if (inner is ObjectDisposedException disposed && disposed.ObjectName == "Http2ResponseBodySource")
                            unobserved.Enqueue(inner);
                    }
                };

                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                TaskScheduler.UnobservedTaskException += handler;
                try
                {
                    // The soak harness saw these on every abandoned HTTP/2 body: disposing faults the
                    // trailers task, which nobody awaits, and its finalizer reported it.
                    await PoolLeakAbandonStreamedBodiesAsync(conn, serverCodec, 8, cts.Token);
                    await PoolLeakPingBarrierAsync(serverCodec, cts.Token);

                    for (int i = 0; i < 3; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
                finally
                {
                    TaskScheduler.UnobservedTaskException -= handler;
                    conn.Dispose();
                }

                Assert.AreEqual(0, unobserved.Count,
                    "a disposed body source left a faulted, unobserved trailers task behind");
            });
        }

        [Test]
        public void FailAllStreams_AfterDisposeFinished_DoesNotThrowObjectDisposed()
        {
            // The soak's reset profile: a body reader's WINDOW_UPDATE write fails after the connection
            // finished disposing (its _cts already disposed); the reader got ObjectDisposedException
            // from CancellationTokenSource.Cancel instead of the connection's network error.
            var duplex = new TestDuplexStream();
            var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443, new Http2Options(), new StreamingOptions());
            conn.Dispose(); // no background loop ran, so the resources (and _cts) are disposed synchronously

            var failAll = typeof(Http2Connection).GetMethod("FailAllStreams", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(failAll);
            Assert.DoesNotThrow(() => failAll.Invoke(conn, new object[] { new IOException("write after shutdown") }));
            Assert.IsFalse(conn.IsAlive);
        }

        // Separate, non-inlined: no local of the caller keeps a body source (or its trailers task) reachable.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task PoolLeakAbandonStreamedBodiesAsync(
            Http2Connection conn, Http2FrameCodec serverCodec, int count, CancellationToken ct)
        {
            for (int i = 0; i < count; i++)
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/abandon/" + i));
                var pending = conn.SendStreamingRequestAsync(request, new RequestContext(request), ct).AsTask();
                var headers = await PoolLeakReadHeadersAsync(serverCodec, ct);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headers.StreamId, 200, endStream: false), ct);
                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "partial", endStream: false), ct);
                using (var response = await WithWatchdogResult(pending, ct))
                {
                    var buffer = new byte[16];
                    Assert.Greater(await response.Body.ReadAsync(buffer, 0, buffer.Length, ct), 0);
                }

                await ReadFrameMatchingAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == headers.StreamId, ct);
            }
        }

        private static Task<UHttpResponse> PoolLeakGetAsync(Http2Connection conn, string path, CancellationToken ct)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com" + path));
            return conn.SendRequestAsync(request, new RequestContext(request), ct).AsTask();
        }

        private static Task<Http2Frame> PoolLeakReadHeadersAsync(Http2FrameCodec serverCodec, CancellationToken ct) =>
            ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, ct);

        /// <summary>
        /// The read loop handles frames in order and acknowledges a PING only after every earlier frame's
        /// handler returned (and released its stream lease), so the ACK proves the loop is idle.
        /// </summary>
        private static async Task PoolLeakPingBarrierAsync(Http2FrameCodec serverCodec, CancellationToken ct)
        {
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Ping,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = (byte[])PoolLeakPingPayload.Clone(),
                Length = 8
            }, ct);
            await ReadFrameMatchingAsync(serverCodec,
                f => f.Type == Http2FrameType.Ping && f.HasFlag(Http2FrameFlags.Ack) &&
                     f.Length == 8 && f.Payload[0] == 1 && f.Payload[1] == 1 && f.Payload[2] == 4, ct);
        }

        private static async Task PoolLeakAssertFailsAsync(Task task, CancellationToken watchdog)
        {
            try
            {
                await WithWatchdog(task, watchdog);
            }
            catch (UHttpException)
            {
                return;
            }

            Assert.Fail("Expected the request to fail with UHttpException.");
        }

        /// <summary>The connection's streams while they are all open (each request's HEADERS were read).</summary>
        private static Http2Stream[] PoolLeakCaptureStreams(Http2Connection conn, int expected)
        {
            var active = GetActiveStreams(conn);
            Assert.AreEqual(expected, active.Count);
            var streams = new Http2Stream[active.Count];
            active.Values.CopyTo(streams, 0);
            return streams;
        }

        /// <summary>
        /// Exact per-connection check: a stream that went back to the pool was reset (no connection, no
        /// lifetime reference); one still bound to this connection with a live reference leaked. The pool
        /// is process-wide, so a later rent of the same object by another connection does not count.
        /// </summary>
        private static void PoolLeakAssertReleased(Http2Connection conn, Http2Stream[] streams, string when)
        {
            var connectionField = typeof(Http2Stream).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
            var lifetimeField = typeof(Http2Stream).GetField("_lifetimeRefCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(connectionField);
            Assert.IsNotNull(lifetimeField);
            foreach (var stream in streams)
            {
                bool stillOwned = ReferenceEquals(connectionField.GetValue(stream), conn) && (int)lifetimeField.GetValue(stream) > 0;
                Assert.IsFalse(stillOwned, "an Http2Stream was not returned to the pool " + when);
            }
        }
    }
}
