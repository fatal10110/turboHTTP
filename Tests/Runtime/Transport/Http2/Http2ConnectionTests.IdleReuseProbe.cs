using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Http2Options.ReuseProbeIdleThreshold (#238): one PING (RFC 9113 Section 6.7) before a request
    /// reuses a connection idle past the threshold. Idleness is set by backdating the connection's last
    /// inbound activity, never by waiting. The transport-level replay is in
    /// RawSocketTransportHttp2ConnectionManagementTests.IdleProbeAndExtraConnections.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        private static readonly TimeSpan ProbeThreshold = TimeSpan.FromHours(1);

        [Test]
        public void IdleReuseProbe_DeadIdleConnection_FailsUnsentAsNotProcessed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.Zero,
                    PingTimeout = TimeSpan.FromMilliseconds(200),
                    ReuseProbeIdleThreshold = ProbeThreshold
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);
                conn.BackdateInboundActivityForTest(ProbeThreshold + ProbeThreshold);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();

                // The probe goes out first; the peer never answers.
                var ping = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, ping.Type);
                Assert.IsFalse(ping.HasFlag(Http2FrameFlags.Ack));

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(() => task);
                var retryable = RawSocketTransport.FindH2Retryable(ex);
                Assert.IsNotNull(retryable, "the request must be replayable on a fresh connection");
                Assert.IsTrue(retryable.RequestNotProcessed, "nothing was sent for the request");
                Assert.IsFalse(conn.IsAlive, "the dead connection is closed");

                // No HEADERS reached the peer before the connection closed (a GOAWAY may precede EOF).
                while (true)
                {
                    Http2Frame frame;
                    try
                    {
                        frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    }
                    catch (System.IO.IOException)
                    {
                        break;
                    }

                    Assert.AreNotEqual(Http2FrameType.Headers, frame.Type, "the request must not be sent");
                }

                conn.Dispose();
            });
        }

        [Test]
        public void IdleReuseProbe_AckedProbe_RequestUsesConnection_NextRequestSendsNoPing()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.Zero,
                    ReuseProbeIdleThreshold = ProbeThreshold
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);
                conn.BackdateInboundActivityForTest(ProbeThreshold + ProbeThreshold);

                // Two requests arrive on the idle connection together: they share one PING.
                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();

                var ping = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, ping.Type);
                Assert.IsFalse(taskA.IsCompleted || taskB.IsCompleted, "requests wait for the probe");
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

                var first = await serverCodec.ReadFrameAsync(16384, cts.Token);
                var second = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, first.Type, "one PING only");
                Assert.AreEqual(Http2FrameType.Headers, second.Type, "one PING only");
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(first.StreamId, 200, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(second.StreamId, 200, endStream: true), cts.Token);
                using (var responseA = await taskA)
                    Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);
                using (var responseB = await taskB)
                    Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);

                // The ACK and the responses were inbound activity: no probe for the next request.
                var requestC = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/c"));
                var taskC = conn.SendRequestAsync(requestC, new RequestContext(requestC), cts.Token).AsTask();
                var headersC = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersC.Type);
                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(headersC.StreamId, 200, endStream: true), cts.Token);
                using (var responseC = await taskC)
                    Assert.AreEqual(HttpStatusCode.OK, responseC.StatusCode);

                Assert.IsTrue(conn.AcceptsNewStreams);
                conn.Dispose();
            });
        }

        [Test]
        public void IdleReuseProbe_DefaultOptions_IdleConnectionSendsNoPing()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options { PingInterval = TimeSpan.Zero };
                Assert.AreEqual(TimeSpan.Zero, options.ReuseProbeIdleThreshold, "off by default");
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);
                conn.BackdateInboundActivityForTest(TimeSpan.FromDays(1));

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
        public void IdleReuseProbe_RecentlyActiveConnection_SendsNoPing()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var options = new Http2Options
                {
                    PingInterval = TimeSpan.Zero,
                    ReuseProbeIdleThreshold = ProbeThreshold
                };
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token, options);
                var serverCodec = new Http2FrameCodec(serverStream);

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
        public void ReuseProbeIdleThreshold_RejectsNegative_CopiedByClone()
        {
            var options = new Http2Options();
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ReuseProbeIdleThreshold = TimeSpan.FromSeconds(-1));

            options.ReuseProbeIdleThreshold = TimeSpan.FromSeconds(30);
            Assert.IsFalse(options.IsDefault());
            Assert.AreEqual(TimeSpan.FromSeconds(30), options.Clone().ReuseProbeIdleThreshold);
        }
    }
}
