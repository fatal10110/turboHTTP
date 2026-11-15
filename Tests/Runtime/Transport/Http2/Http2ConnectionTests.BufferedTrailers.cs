using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        // #539: HEADERS, DATA and trailing HEADERS delivered in one read used to let the body source
        // detach (stream already complete) and the buffered collector built the response without the
        // trailers. The response-start callback is held until the read loop has processed the whole
        // flight (a PING queued after it is acknowledged), so the detach attempt always sees a
        // completed stream.
        [Test]
        public void SendBuffered_TrailersInSameFlight_ResponseKeepsTrailers()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/trailers"));
                var context = new RequestContext(request);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                DetachProbeBodySource probe = null;

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    (dispatchRequest, handler, dispatchContext, ct) =>
                    {
                        var gated = new GatedResponseStartHandler(handler, gate.Task, p => probe = p);
                        gated.OnRequestStart(dispatchRequest, dispatchContext);
                        return conn.DispatchAsync(dispatchRequest, gated, dispatchContext, ct);
                    },
                    request,
                    context,
                    cts.Token);

                var requestHeaders = await ReadMatchingFrameAsync(
                    serverCodec, f => f.Type == Http2FrameType.Headers, 5000);
                int streamId = requestHeaders.StreamId;

                await WriteFlightAsync(
                    serverStream,
                    cts.Token,
                    BuildResponseHeadersFrame(streamId, 200),
                    BuildDataFrame(streamId, "payload", endStream: false),
                    BuildTrailingHeadersFrame(
                        streamId,
                        new Dictionary<string, string> { { "grpc-status", "0" }, { "x-trailer", "ok" } }),
                    BuildPingFrame());

                await ReadMatchingFrameAsync(
                    serverCodec, f => f.Type == Http2FrameType.Ping && f.HasFlag(Http2FrameFlags.Ack), 5000);
                gate.SetResult(true);

                using var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("payload", response.GetBodyAsString());
                Assert.AreEqual("0", response.Trailers.Get("grpc-status"));
                Assert.AreEqual("ok", response.Trailers.Get("x-trailer"));
                Assert.IsNotNull(probe);
                Assert.AreEqual(false, probe.DetachResult, "A source with trailers must not detach.");

                conn.Dispose();
            });
        }

        // The zero-copy detach stays in use for a complete response without trailers.
        [Test]
        public void SendBuffered_NoTrailersInSameFlight_BodyDetachesWithEmptyTrailers()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/no-trailers"));
                var context = new RequestContext(request);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                DetachProbeBodySource probe = null;

                var responseTask = TransportDispatchHelper.CollectResponseAsync(
                    (dispatchRequest, handler, dispatchContext, ct) =>
                    {
                        var gated = new GatedResponseStartHandler(handler, gate.Task, p => probe = p);
                        gated.OnRequestStart(dispatchRequest, dispatchContext);
                        return conn.DispatchAsync(dispatchRequest, gated, dispatchContext, ct);
                    },
                    request,
                    context,
                    cts.Token);

                var requestHeaders = await ReadMatchingFrameAsync(
                    serverCodec, f => f.Type == Http2FrameType.Headers, 5000);
                int streamId = requestHeaders.StreamId;

                await WriteFlightAsync(
                    serverStream,
                    cts.Token,
                    BuildResponseHeadersFrame(streamId, 200),
                    BuildDataFrame(streamId, "payload", endStream: true),
                    BuildPingFrame());

                await ReadMatchingFrameAsync(
                    serverCodec, f => f.Type == Http2FrameType.Ping && f.HasFlag(Http2FrameFlags.Ack), 5000);
                gate.SetResult(true);

                using var response = await responseTask;
                Assert.AreEqual("payload", response.GetBodyAsString());
                Assert.AreEqual(0, response.Trailers.Count);
                Assert.IsNotNull(probe);
                Assert.AreEqual(true, probe.DetachResult);

                conn.Dispose();
            });
        }

        // Serializes every frame into one buffer and writes it with a single write, so the client
        // receives the whole flight in one read.
        private static async Task WriteFlightAsync(Stream serverStream, CancellationToken ct, params Http2Frame[] frames)
        {
            using var flight = new MemoryStream();
            var flightCodec = new Http2FrameCodec(flight);
            foreach (var frame in frames)
                await flightCodec.WriteFrameAsync(frame, ct);

            var bytes = flight.ToArray();
            await serverStream.WriteAsync(bytes, 0, bytes.Length, ct);
            await serverStream.FlushAsync(ct);
        }

        private static Http2Frame BuildPingFrame() => new Http2Frame
        {
            Type = Http2FrameType.Ping,
            Flags = Http2FrameFlags.None,
            StreamId = 0,
            Payload = new byte[8],
            Length = 8
        };

        // Holds the response-start callback until the test releases it, and wraps the body source to
        // record whether the collector's detach attempt succeeded.
        private sealed class GatedResponseStartHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private readonly Task _gate;
            private readonly Action<DetachProbeBodySource> _onProbe;

            public GatedResponseStartHandler(IHttpHandler inner, Task gate, Action<DetachProbeBodySource> onProbe)
            {
                _inner = inner;
                _gate = gate;
                _onProbe = onProbe;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context) =>
                _inner.OnRequestStart(request, context);

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                await _gate.ConfigureAwait(false);
                var probe = new DetachProbeBodySource(body);
                _onProbe(probe);
                await _inner.OnResponseStartAsync(statusCode, headers, probe, context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context) =>
                _inner.OnResponseError(error, context);
        }

        private sealed class DetachProbeBodySource : IResponseBodySource
        {
            private readonly IResponseBodySource _inner;

            public DetachProbeBodySource(IResponseBodySource inner) => _inner = inner;

            public bool? DetachResult { get; private set; }

            public long? Length => _inner.Length;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data) => _inner.TryGetBufferedData(out data);

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                var detached = _inner.TryDetachBufferedBody(out body);
                DetachResult = detached;
                return detached;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct) =>
                _inner.ReadAsync(destination, ct);

            public ValueTask DrainAsync(CancellationToken ct) => _inner.DrainAsync(ct);

            public void Abort() => _inner.Abort();

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) => _inner.GetTrailersAsync(ct);

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
