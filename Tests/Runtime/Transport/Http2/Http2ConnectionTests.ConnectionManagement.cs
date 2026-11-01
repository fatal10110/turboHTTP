using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Connection management (#16 admission, #17 GOAWAY drain, #22 PING liveness, #15 stream-scoped
    /// errors) at the Http2Connection level. Transport replay is covered by
    /// RawSocketTransportHttp2ConnectionManagementTests.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        [Test]
        public void Admission_ThreeHundredGets_PeerLimitHundred_AllSucceedWithinLimit()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(30000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                await SendMaxConcurrentStreamsAndAwaitAckAsync(serverCodec, 100, cts.Token);

                const int total = 300;
                const int limit = 100;
                var tasks = new Task<UHttpResponse>[total];
                for (int i = 0; i < total; i++)
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a/" + i));
                    tasks[i] = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                }

                // The server answers in batches of exactly `limit`: a client that opened stream 101 while
                // 100 were open would break the assertion (RFC 9113 Section 5.1.2).
                var open = new List<int>(limit);
                int seen = 0;
                while (seen < total)
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    if (frame.Type != Http2FrameType.Headers)
                        continue;

                    seen++;
                    open.Add(frame.StreamId);
                    Assert.LessOrEqual(open.Count, limit, "client exceeded the peer's MAX_CONCURRENT_STREAMS");
                    if (open.Count == limit || seen == total)
                    {
                        foreach (var streamId in open)
                        {
                            await serverCodec.WriteFrameAsync(
                                BuildResponseHeadersFrame(streamId, 200, endStream: true),
                                cts.Token);
                        }

                        open.Clear();
                    }
                }

                foreach (var task in tasks)
                {
                    using var response = await task;
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                }

                Assert.IsTrue(conn.IsAlive);
                Assert.AreEqual(0, conn.QueuedAdmissionCount);
                conn.Dispose();
            });
        }

        [Test]
        public void Admission_SettingsLowersLimit_OpenStreamsKeptAndNewStreamWaitsForSlot()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                int streamA = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;
                int streamB = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                // RFC 9113 Section 6.5.2: lowering the limit does not close open streams; new ones wait.
                await SendMaxConcurrentStreamsAndAwaitAckAsync(serverCodec, 1, cts.Token);

                var requestC = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/c"));
                var taskC = conn.SendRequestAsync(requestC, new RequestContext(requestC), cts.Token).AsTask();
                await WaitForConditionAsync(() => conn.QueuedAdmissionCount == 1, cts.Token);
                Assert.IsFalse(taskC.IsCompleted, "a request over the limit must wait, not fail");

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamA, 200, endStream: true), cts.Token);
                using (var responseA = await taskA)
                    Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);

                // Still one open stream (B) against a limit of 1.
                Assert.AreEqual(1, conn.QueuedAdmissionCount);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamB, 200, endStream: true), cts.Token);
                using (var responseB = await taskB)
                    Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);

                var headersC = await ReadHeadersFrameAsync(serverCodec, cts.Token);
                Assert.Greater(headersC.StreamId, streamB);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersC.StreamId, 200, endStream: true),
                    cts.Token);
                using (var responseC = await taskC)
                    Assert.AreEqual(HttpStatusCode.OK, responseC.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void Admission_QueuedRequestCancelled_LeavesQueueAndConsumesNoStreamId()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                await SendMaxConcurrentStreamsAndAwaitAckAsync(serverCodec, 1, cts.Token);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                int streamA = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                using var queuedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), queuedCts.Token).AsTask();
                await WaitForConditionAsync(() => conn.QueuedAdmissionCount == 1, cts.Token);

                queuedCts.Cancel();
                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(() => taskB);
                Assert.AreEqual(0, conn.QueuedAdmissionCount);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamA, 200, endStream: true), cts.Token);
                using (var responseA = await taskA)
                    Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);

                var requestC = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/c"));
                var taskC = conn.SendRequestAsync(requestC, new RequestContext(requestC), cts.Token).AsTask();
                var headersC = await ReadHeadersFrameAsync(serverCodec, cts.Token);
                Assert.AreEqual(streamA + 2, headersC.StreamId, "the cancelled queued request must not take a stream id");
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersC.StreamId, 200, endStream: true),
                    cts.Token);
                using (var responseC = await taskC)
                    Assert.AreEqual(HttpStatusCode.OK, responseC.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void RstStreamRefused_FailsOnlyThatStream_AsNotProcessed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var requestA = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/a"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                int streamA = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;
                int streamB = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                await serverCodec.WriteFrameAsync(BuildRstStreamFrame(streamA, Http2ErrorCode.RefusedStream), cts.Token);
                var refused = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(() => taskA);
                Assert.IsTrue(refused.RequestNotProcessed, "REFUSED_STREAM means not processed (RFC 9113 Section 8.7)");

                // RFC 9113 Section 5.4.2: the stream error leaves the connection and its other streams alone.
                Assert.IsTrue(conn.AcceptsNewStreams);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamB, 200, endStream: true), cts.Token);
                using (var responseB = await taskB)
                    Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void RstStreamInternalError_IsNotMarkedRetryable()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                int streamId = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                await serverCodec.WriteFrameAsync(BuildRstStreamFrame(streamId, Http2ErrorCode.InternalError), cts.Token);
                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(() => task);
                Assert.IsNotInstanceOf<Http2RetryableRequestException>(ex);
                Assert.IsTrue(conn.AcceptsNewStreams);

                conn.Dispose();
            });
        }

        [Test]
        public void GoAwayNoError_DrainsStreamsAtOrBelowLastIdThenCloses()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var tasks = new Task<UHttpResponse>[4];
                var streamIds = new int[4];
                for (int i = 0; i < tasks.Length; i++)
                {
                    var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/p/" + i));
                    tasks[i] = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                    streamIds[i] = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;
                }

                await serverCodec.WriteFrameAsync(BuildGoAwayFrameForTest(streamIds[1], Http2ErrorCode.NoError), cts.Token);

                // Streams above the last stream id were not processed (RFC 9113 Section 6.8, 8.7).
                for (int i = 2; i < 4; i++)
                {
                    var ex = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(() => tasks[i]);
                    Assert.IsTrue(ex.RequestNotProcessed);
                }

                // Draining: alive for the streams it may still process, closed to new streams.
                Assert.IsTrue(conn.IsAlive, "GOAWAY(NO_ERROR) must not abort in-flight streams");
                Assert.IsFalse(conn.AcceptsNewStreams);
                var late = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/late"));
                var lateEx = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(
                    () => conn.SendRequestAsync(late, new RequestContext(late), cts.Token).AsTask());
                Assert.IsTrue(lateEx.RequestNotProcessed);

                // A second GOAWAY may lower the last stream id, never raise it.
                await serverCodec.WriteFrameAsync(BuildGoAwayFrameForTest(int.MaxValue, Http2ErrorCode.NoError), cts.Token);

                for (int i = 0; i < 2; i++)
                {
                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(streamIds[i], 200, endStream: true),
                        cts.Token);
                    using var response = await tasks[i];
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                }

                // Drained: the connection closes itself.
                await WaitForConditionAsync(() => !conn.IsAlive, cts.Token);
                conn.Dispose();
            });
        }

        [Test]
        public void GoAway_RefusesQueuedAdmissionsAsNotProcessed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                await SendMaxConcurrentStreamsAndAwaitAckAsync(serverCodec, 1, cts.Token);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                int streamA = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                var requestB = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                await WaitForConditionAsync(() => conn.QueuedAdmissionCount == 1, cts.Token);

                await serverCodec.WriteFrameAsync(BuildGoAwayFrameForTest(streamA, Http2ErrorCode.NoError), cts.Token);
                var ex = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(() => taskB);
                Assert.IsTrue(ex.RequestNotProcessed);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamA, 200, endStream: true), cts.Token);
                using (var responseA = await taskA)
                    Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void Liveness_PeerStopsResponding_ConnectionFailsWithinPingTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.FromMilliseconds(20),
                    PingTimeout = TimeSpan.FromMilliseconds(200)
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/hang"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                await ReadHeadersFrameAsync(serverCodec, cts.Token);

                // The peer reads the PING but never answers (half-dead socket, no FIN).
                Http2Frame ping;
                do
                {
                    ping = await serverCodec.ReadFrameAsync(16384, cts.Token);
                }
                while (ping.Type != Http2FrameType.Ping);

                Assert.IsFalse(ping.HasFlag(Http2FrameFlags.Ack));
                Assert.AreEqual(8, ping.Length);
                Assert.IsFalse(IsAllZero(ping.Payload, 8), "the PING payload must identify the probe");

                var ex = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(() => task);
                Assert.IsFalse(ex.RequestNotProcessed, "a dead connection is a connection failure, not a refusal");
                StringAssert.Contains("PING", ex.HttpError.Message);
                Assert.IsFalse(conn.IsAlive);
                Assert.IsFalse(conn.AcceptsNewStreams);
                conn.Dispose();
            });
        }

        [Test]
        public void Liveness_AckEchoingPayload_KeepsConnectionAlive()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.Zero, // loop off: the test drives one cycle
                    PingTimeout = TimeSpan.FromSeconds(5)
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                int streamId = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                var cycle = conn.RunLivenessCycleAsync(cts.Token);
                var ping = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, ping.Type);

                // An ACK with a different payload does not count (RFC 9113 Section 6.7).
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = new byte[8],
                    Length = 8
                }, cts.Token);
                Assert.IsFalse(cycle.IsCompleted);

                var echo = new byte[8];
                Array.Copy(ping.Payload, echo, 8);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = echo,
                    Length = 8
                }, cts.Token);

                Assert.AreEqual(Http2Connection.LivenessCycleResult.Acknowledged, await cycle);
                Assert.IsTrue(conn.AcceptsNewStreams);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
                using (var response = await task)
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void Liveness_IdleConnection_SendsNoPing()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options { PingInterval = TimeSpan.Zero };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                Assert.AreEqual(
                    Http2Connection.LivenessCycleResult.Idle,
                    await conn.RunLivenessCycleAsync(cts.Token));

                // Nothing was written: the next frame the peer sees is the request's HEADERS.
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var first = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, first.Type);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(first.StreamId, 200, endStream: true), cts.Token);
                using (var response = await task)
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void Admission_LimitLoweredWhileReservedRequestWaitsForWriteLock_RefusedUnsent()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                await SendMaxConcurrentStreamsAndAwaitAckAsync(serverCodec, 2, cts.Token);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                int streamA = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var writeLock = (SemaphoreSlim)typeof(Http2Connection).GetField("_writeLock", flags).GetValue(conn);
                var remoteSettings = (Http2Settings)typeof(Http2Connection).GetField("_remoteSettings", flags).GetValue(conn);
                var pendingField = typeof(Http2Connection).GetField("_pendingAdmissions", flags);

                // B reserves the second slot, then waits for the write lock while the peer lowers the
                // limit to 1 (RFC 9113 Section 5.1.2): opening B would exceed the applied limit.
                await writeLock.WaitAsync(cts.Token);
                Task<UHttpResponse> taskB;
                try
                {
                    var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                    taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                    await WaitForConditionAsync(() => (int)pendingField.GetValue(conn) == 1, cts.Token);

                    var payload = BuildSettingsPayload(Http2SettingId.MaxConcurrentStreams, 1);
                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Settings,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = payload,
                        Length = payload.Length
                    }, cts.Token);
                    await WaitForConditionAsync(() => remoteSettings.MaxConcurrentStreams == 1, cts.Token);
                }
                finally
                {
                    writeLock.Release();
                }

                var nextHeaders = ReadHeadersFrameAsync(serverCodec, cts.Token);
                var first = await Task.WhenAny(taskB, nextHeaders);
                Assert.AreSame(taskB, first, "the request must be refused, not opened over the lowered limit");
                var refused = await TestHelpers.AssertThrowsAsync<Http2RetryableRequestException>(() => taskB);
                Assert.IsTrue(refused.RequestNotProcessed, "an unsent request is replayable for any method");
                Assert.AreEqual(0, (int)pendingField.GetValue(conn), "the reservation is released");

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamA, 200, endStream: true), cts.Token);
                using (var responseA = await taskA)
                    Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);

                // B consumed no stream id: the next request opens streamA + 2.
                var requestC = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/c"));
                var taskC = conn.SendRequestAsync(requestC, new RequestContext(requestC), cts.Token).AsTask();
                var headersC = await nextHeaders;
                Assert.AreEqual(streamA + 2, headersC.StreamId);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersC.StreamId, 200, endStream: true),
                    cts.Token);
                using (var responseC = await taskC)
                    Assert.AreEqual(HttpStatusCode.OK, responseC.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void ClassifyDispatchFailure_MarksReplayOnlyBeforeResponseStart()
        {
            var error = new UHttpError(UHttpErrorType.NetworkError, "refused");
            var notProcessed = new Http2RetryableRequestException(error, requestNotProcessed: true);

            // Unprocessed before response start: the marker is kept (replay allowed for any method).
            Assert.AreSame(notProcessed, Http2Connection.ClassifyDispatchFailure(notProcessed, false, false));

            // Once the response started nothing is replayable: the marker is stripped, the error kept.
            var stripped = Http2Connection.ClassifyDispatchFailure(notProcessed, true, true);
            Assert.IsNotInstanceOf<Http2RetryableRequestException>(stripped);
            Assert.AreSame(error, ((UHttpException)stripped).HttpError);

            // A dispatch failing because the connection closed: connection-lost marker before response
            // start only.
            var lost = Http2Connection.ClassifyDispatchFailure(new OperationCanceledException(), false, true);
            Assert.IsInstanceOf<Http2RetryableRequestException>(lost);
            Assert.IsFalse(((Http2RetryableRequestException)lost).RequestNotProcessed);
            Assert.IsNotInstanceOf<Http2RetryableRequestException>(
                Http2Connection.ClassifyDispatchFailure(new OperationCanceledException(), true, true));

            // A stream-scoped failure on a working connection is never marked (#15).
            var streamScoped = Http2Connection.ClassifyDispatchFailure(new System.IO.IOException("body"), false, false);
            Assert.IsInstanceOf<UHttpException>(streamScoped);
            Assert.IsNotInstanceOf<Http2RetryableRequestException>(streamScoped);
        }

        [Test]
        public void Liveness_AckDelayedButFramesArriving_ConnectionStaysAlive()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.Zero, // loop off: the test drives one cycle
                    PingTimeout = TimeSpan.FromMilliseconds(1500)
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                int streamId = (await ReadHeadersFrameAsync(serverCodec, cts.Token)).StreamId;

                var cycle = conn.RunLivenessCycleAsync(cts.Token);
                var ping = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, ping.Type);

                // The peer keeps sending (its ACK queued behind DATA on a slow link) but never ACKs in
                // time: frames arriving after the PING prove the connection alive.
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = new byte[] { 0, 0, 0, 1 },
                    Length = 4
                }, cts.Token);

                Assert.AreEqual(Http2Connection.LivenessCycleResult.PeerActive, await cycle);
                Assert.IsTrue(conn.IsAlive);
                Assert.IsTrue(conn.AcceptsNewStreams);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
                using (var response = await task)
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        private static async Task SendMaxConcurrentStreamsAndAwaitAckAsync(
            Http2FrameCodec serverCodec,
            uint maxConcurrentStreams,
            CancellationToken ct)
        {
            var payload = BuildSettingsPayload(Http2SettingId.MaxConcurrentStreams, maxConcurrentStreams);
            await serverCodec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = payload,
                Length = payload.Length
            }, ct);

            // The client ACKs after applying the settings (RFC 9113 Section 6.5.3).
            while (true)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.Settings && frame.HasFlag(Http2FrameFlags.Ack))
                    return;
            }
        }

        private static async Task<Http2Frame> ReadHeadersFrameAsync(Http2FrameCodec serverCodec, CancellationToken ct)
        {
            while (true)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.Headers)
                    return frame;
            }
        }

        private static Http2Frame BuildGoAwayFrameForTest(int lastStreamId, Http2ErrorCode errorCode)
        {
            uint code = (uint)errorCode;
            var payload = new[]
            {
                (byte)((lastStreamId >> 24) & 0x7F), (byte)(lastStreamId >> 16), (byte)(lastStreamId >> 8), (byte)lastStreamId,
                (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code
            };
            return new Http2Frame
            {
                Type = Http2FrameType.GoAway,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = payload,
                Length = payload.Length
            };
        }

        // Waits for a state change made by another task; bounded by the test's token, never a fixed sleep.
        private static async Task WaitForConditionAsync(Func<bool> condition, CancellationToken ct)
        {
            while (!condition())
                await Task.Delay(1, ct);
        }

        private static bool IsAllZero(byte[] buffer, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] != 0)
                    return false;
            }

            return true;
        }
    }
}
