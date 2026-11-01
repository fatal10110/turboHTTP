using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    // #197: a frame write the peer stops accepting is bounded at the connection level.
    public partial class Http2ConnectionTests
    {
        private const int SendStallTimeoutMilliseconds = 200;
        private const int SendStallMaintenanceIntervalMilliseconds = 50;
        private const int FrameHeaderBytes = 9;

        [Test]
        public void DataFrameWriteBlockedByNonReadingPeer_FailsConnectionAndEveryStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, gate) = await CreateGatedConnectionAsync(cts.Token, CreateSendStallOptions());
                var serverCodec = new Http2FrameCodec(serverStream);

                // Stream A: HEADERS on the wire, waiting for its response.
                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), CancellationToken.None).AsTask();
                await ReadHeaderBlockAsync(serverCodec, cts.Token);

                // Stream B: the DATA frame header is written, then the peer stops reading. The write
                // is never released: only the connection-level stall bound can end it.
                var body = Enumerable.Repeat((byte)'b', 777).ToArray();
                var payloadHeld = gate.BlockWrite(count => count == body.Length);
                var requestB = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/b"))
                    .WithBody(body);
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), CancellationToken.None).AsTask();
                await ReadHeaderBlockAsync(serverCodec, cts.Token);
                await WithWatchdog(payloadHeld, cts.Token);

                await AssertNetworkErrorAsync(taskB, cts.Token);
                await AssertNetworkErrorAsync(taskA, cts.Token);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void RequestDeadlineExpiresWhileHeadersWriteBlocked_CompletesWithinSendStallBound()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var requestCts = new CancellationTokenSource();
                var (conn, _, gate) = await CreateGatedConnectionAsync(cts.Token, CreateSendStallOptions());

                // The first write larger than a frame header is this request's HEADERS payload.
                var headersHeld = gate.BlockWrite(count => count > FrameHeaderBytes);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/deadline"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), requestCts.Token).AsTask();
                await WithWatchdog(headersHeld, cts.Token);

                // The request deadline expires mid-frame. The request token must not abort the
                // started frame (RFC 9113 Section 4.1), so the request completes once the stall
                // bound fails the connection.
                requestCts.Cancel();
                await AssertCancelledAsync(task, cts.Token);
                Assert.IsFalse(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void FrameCodec_WriteStall_ReportedOnlyWhileAFrameWriteIsInFlight()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var gate = new GatedWriteStream(new MemoryStream());
                var codec = new Http2FrameCodec(gate);
                const long timeoutMs = 60000;
                var payload = new byte[100];

                Assert.IsFalse(codec.IsWriteStalled(FarFutureTimestamp(), timeoutMs), "an idle codec is never stalled");

                var held = gate.BlockWrite(count => count == payload.Length);
                var write = codec.WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, payload, cts.Token);
                await WithWatchdog(held, cts.Token);
                Assert.IsFalse(codec.IsWriteStalled(Http2MonotonicClock.GetTimestamp(), timeoutMs),
                    "progress was just recorded");
                Assert.IsTrue(codec.IsWriteStalled(FarFutureTimestamp(), timeoutMs),
                    "a held write is stalled once the timeout passes without progress");

                gate.Release();
                await WithWatchdog(write, cts.Token);
                Assert.IsFalse(codec.IsWriteStalled(FarFutureTimestamp(), timeoutMs), "a completed write clears the marker");

                using var writeCts = new CancellationTokenSource();
                held = gate.BlockWrite(count => count == payload.Length);
                write = codec.WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, payload, writeCts.Token);
                await WithWatchdog(held, cts.Token);
                writeCts.Cancel();
                await AssertCancelledAsync(write, cts.Token);
                Assert.IsFalse(codec.IsWriteStalled(FarFutureTimestamp(), timeoutMs), "a failed write clears the marker");
            });
        }

        [Test]
        public void FrameCodec_LargePayload_WrittenInProgressSlices()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var inner = new MemoryStream();
                var gate = new GatedWriteStream(inner);
                var codec = new Http2FrameCodec(gate);
                var payload = new byte[2 * Http2FrameCodec.WriteProgressSliceBytes + 7232];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)i;

                var writeSizes = new List<int>();
                gate.BlockWrite(count =>
                {
                    writeSizes.Add(count);
                    return false;
                });

                await WithWatchdog(
                    codec.WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, payload, cts.Token),
                    cts.Token);

                CollectionAssert.AreEqual(
                    new[] { FrameHeaderBytes, Http2FrameCodec.WriteProgressSliceBytes, Http2FrameCodec.WriteProgressSliceBytes, 7232 },
                    writeSizes);
                var written = inner.ToArray();
                Assert.AreEqual(FrameHeaderBytes + payload.Length, written.Length);
                CollectionAssert.AreEqual(payload, written.Skip(FrameHeaderBytes).ToArray());
            });
        }

        private static Http2Options CreateSendStallOptions() =>
            CreateTestHttp2Options(
                stallTimeoutMilliseconds: SendStallTimeoutMilliseconds,
                maintenanceIntervalMilliseconds: SendStallMaintenanceIntervalMilliseconds);

        private static long FarFutureTimestamp() =>
            Http2MonotonicClock.GetTimestamp() + Stopwatch.Frequency * 120;

        private static async Task AssertNetworkErrorAsync(Task task, CancellationToken watchdog)
        {
            try
            {
                await WithWatchdog(task, watchdog);
            }
            catch (UHttpException ex)
            {
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                return;
            }

            Assert.Fail("Expected the request to fail with UHttpException(NetworkError).");
        }
    }
}
