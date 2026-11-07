using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// The response handler chain runs off the read loop (#29): a handler that blocks synchronously in
    /// OnResponseStartAsync must not stall frame processing for the other streams of the connection.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        [Test]
        public void HandlerOffLoop_BlockingResponseStartHandler_DoesNotDelayConcurrentStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var slowHandler = new BlockingResponseStartHandler();

                try
                {
                    var slowRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/slow"));
                    var slowDispatch = conn.DispatchAsync(
                        slowRequest, slowHandler, new RequestContext(slowRequest), cts.Token);
                    var slowHeaders = await ReadFrameMatchingAsync(
                        serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                    var fastRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/fast"));
                    var fastHandler = new RecordingHandler();
                    var fastDispatch = conn.DispatchAsync(
                        fastRequest, fastHandler, new RequestContext(fastRequest), cts.Token);
                    var fastHeaders = await ReadFrameMatchingAsync(
                        serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                    // The slow stream's response arrives first; its handler blocks (the issue's "2 s"
                    // block is a gate the test opens only after the other stream completed).
                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(slowHeaders.StreamId, 200, endStream: true), cts.Token);
                    await WithWatchdog(slowHandler.Entered, cts.Token);

                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(fastHeaders.StreamId, 204, endStream: true), cts.Token);
                    var pingPayload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Ping,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = pingPayload,
                        Length = pingPayload.Length
                    }, cts.Token);

                    // Before #29 the read loop itself was inside the blocked handler, so neither the
                    // other stream's HEADERS nor the PING was processed until the gate opened.
                    using (var unaffected = new CancellationTokenSource(5000))
                    {
                        await WithWatchdog(fastDispatch, unaffected.Token);
                        var pong = await ReadFrameMatchingAsync(
                            serverCodec,
                            f => f.Type == Http2FrameType.Ping && (f.Flags & Http2FrameFlags.Ack) != 0,
                            unaffected.Token);
                        Assert.AreEqual(pingPayload, pong.Payload.AsSpan(0, pong.Length).ToArray());
                    }

                    Assert.AreEqual(204, fastHandler.StatusCode);
                    Assert.IsNull(fastHandler.Error);
                    Assert.IsFalse(slowDispatch.IsCompleted, "The slow handler is still blocked.");

                    slowHandler.Release();
                    await WithWatchdog(slowDispatch, cts.Token);
                    Assert.AreEqual(200, slowHandler.StatusCode);
                    Assert.IsTrue(conn.IsAlive);
                }
                finally
                {
                    slowHandler.Release();
                    conn.Dispose();
                }
            });
        }

        /// <summary>
        /// Handler whose OnResponseStartAsync blocks its thread synchronously until the test releases it
        /// (bounded, so a failing run never parks a thread forever).
        /// </summary>
        private sealed class BlockingResponseStartHandler : IHttpHandler
        {
            private readonly TaskCompletionSource<bool> _entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

            public Task Entered => _entered.Task;
            public int StatusCode { get; private set; }

            public void Release() => _release.Set();

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode, HttpHeaders headers, IResponseBodySource body, RequestContext context)
            {
                StatusCode = statusCode;
                _entered.TrySetResult(true);
                _release.Wait(TimeSpan.FromSeconds(10));
                body?.Abort();
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }
    }
}
