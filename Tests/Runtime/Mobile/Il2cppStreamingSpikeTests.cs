using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// Phase 22a.6 Step 3 (#102): the 22a step-0 IL2CPP spike as a player test. Each test drives one of the
    /// generic or async shapes the streaming model depends on, through the real types, so a High-stripping
    /// IL2CPP player (Tools~/unity/run-emulator-tests.sh --stripping High) fails here if an instantiation
    /// or a preserved type is missing: <c>IAsyncDisposable</c>/<c>await using</c>, <c>ValueTask&lt;T&gt;</c>
    /// (RequestBodyReadSession, HttpHeaders, int), <c>TaskCompletionSource&lt;HttpHeaders&gt;</c> behind both
    /// transports' trailers, <c>ManualResetValueTaskSourceCore&lt;T&gt;</c> through
    /// <c>SingleReaderChannel&lt;T&gt;</c> past its 16-bit version wrap, and synchronous completion of the
    /// hot-path <c>ReadAsync</c> when data is already buffered (no async state machine box). They are
    /// deterministic and also run in the Editor.
    /// </summary>
    [TestFixture]
    public sealed class Il2cppStreamingSpikeTests
    {
        [Test]
        public void AwaitUsing_StreamingResponse_DisposesTheBodySourceAsynchronously()
        {
            AssertAsync.Run(async () =>
            {
                var payload = Encoding.ASCII.GetBytes("spike-body");
                var body = new MockResponseBodySource(
                    new[] { new ReadOnlyMemory<byte>(payload) },
                    length: payload.Length,
                    trailers: HttpHeaders.Empty);
                using var client = CreateClient(body);

                var buffer = new byte[64];
                int read;
                await using (var response = await client.Get("https://spike.local/body").SendStreamingAsync())
                {
                    read = await response.Body.ReadAsync(buffer, 0, buffer.Length);
                    Assert.AreEqual(0, body.DisposeAsyncCount, "disposed before the await using block ended");
                }

                Assert.AreEqual(payload.Length, read);
                Assert.AreEqual(1, body.DisposeAsyncCount, "await using must call IAsyncDisposable.DisposeAsync once");
            });
        }

        [Test]
        public void GetTrailersAsync_ThroughTheClient_ReturnsValueTaskOfHttpHeaders()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Spike", "trailer");
                var body = new MockResponseBodySource(
                    new[] { new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }) },
                    length: 3,
                    trailers: trailers);
                using var client = CreateClient(body);

                await using var response = await client.Get("https://spike.local/trailers").SendStreamingAsync();
                var buffer = new byte[16];
                while (await response.Body.ReadAsync(buffer, 0, buffer.Length) > 0)
                {
                }

                ValueTask<HttpHeaders> pending = response.GetTrailersAsync();
                Assert.AreEqual("trailer", (await pending).Get("X-Spike"));
            });
        }

        [Test]
        public void Http2BodySource_TrailersAndBufferedReads_UseTaskCompletionSourceAndCompleteSynchronously()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://spike.local/h2"));
                var connection = new Http2Connection(Stream.Null, "spike.local", 443, new Http2Options(), new StreamingOptions());
                var stream = new Http2Stream();
                stream.Initialize(1, request, NoOpHttpHandler.Instance, new RequestContext(request), 65535, 65535, connection);
                var source = new Http2ResponseBodySource(connection, stream, 8, completed: false);
                try
                {
                    var chunk = new byte[] { 1, 2, 3, 4 };
                    Assert.AreEqual(Http2ResponseBodyEnqueueResult.Accepted, source.TryEnqueueData(chunk, 0, 4, flowControlledLength: 0));
                    Assert.AreEqual(Http2ResponseBodyEnqueueResult.Accepted, source.TryEnqueueData(chunk, 0, 4, flowControlledLength: 0));
                    var trailers = new HttpHeaders();
                    trailers.Set("grpc-status", "0");
                    source.SetTrailers(trailers); // TaskCompletionSource<HttpHeaders>.TrySetResult
                    source.Complete();

                    var buffer = new byte[4];
                    for (var i = 0; i < 2; i++)
                    {
                        ValueTask<int> read = source.ReadAsync(buffer, CancellationToken.None);
                        Assert.IsTrue(read.IsCompletedSuccessfully, "a buffered HTTP/2 read must complete synchronously");
                        Assert.AreEqual(4, read.Result);
                    }

                    ValueTask<int> end = source.ReadAsync(buffer, CancellationToken.None);
                    Assert.IsTrue(end.IsCompletedSuccessfully, "end of a completed HTTP/2 body must complete synchronously");
                    Assert.AreEqual(0, end.Result);
                    Assert.AreEqual("0", (await source.GetTrailersAsync(CancellationToken.None)).Get("grpc-status"));
                }
                finally
                {
                    await source.DisposeAsync();
                    request.Dispose();
                    connection.Dispose();
                }
            });
        }

        [Test]
        public void Http11BodySource_ChunkedTrailersAndBufferedReads_UseTaskCompletionSourceAndCompleteSynchronously()
        {
            AssertAsync.Run(async () =>
            {
                var wire = Encoding.ASCII.GetBytes("4\r\nabcd\r\n4\r\nefgh\r\n0\r\nX-Spike: chunked\r\n\r\n");
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var stream = new MemoryStream(wire, writable: false);
                var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
                {
                    StatusCode = HttpStatusCode.OK,
                    Headers = HttpHeaders.Empty,
                    KeepAlive = false,
                    BodyKind = Http11ResponseBodyKind.Chunked
                };
                var lease = new ConnectionLease(null, new SemaphoreSlim(0, 1), new PooledConnection(socket, stream, "spike.local", 80, false));
                await using var source = new Http11ResponseBodySource(head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));
                head.Dispose();

                var buffer = new byte[4];
                var total = 0;
                while (true)
                {
                    // MemoryStream completes every read synchronously, so the whole ReadAsync chain must too.
                    ValueTask<int> read = source.ReadAsync(buffer, CancellationToken.None);
                    Assert.IsTrue(read.IsCompletedSuccessfully, "a buffered HTTP/1.1 read must complete synchronously");
                    if (read.Result == 0)
                        break;
                    total += read.Result;
                }

                Assert.AreEqual(8, total);
                Assert.AreEqual("chunked", (await source.GetTrailersAsync(CancellationToken.None)).Get("X-Spike"));
            });
        }

        [Test]
        public void BufferedResponseBodyStream_ReadAsync_CompletesSynchronously()
        {
            AssertAsync.Run(async () =>
            {
                var payload = new byte[256];
                var body = new MockResponseBodySource(new ReadOnlyMemory<byte>(payload));
                using var client = CreateClient(body);

                await using var response = await client.Get("https://spike.local/buffered").SendStreamingAsync();
                var buffer = new byte[64];
                var total = 0;
                while (true)
                {
                    ValueTask<int> read = response.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                    Assert.IsTrue(read.IsCompletedSuccessfully, "ResponseBodyStream over buffered data must complete synchronously");
                    if (read.Result == 0)
                        break;
                    total += read.Result;
                }

                Assert.AreEqual(payload.Length, total);
            });
        }

        [Test]
        public void OpenReadSessionAsync_ReplayableStreamBody_ValueTaskOfSessionReopensAtTheStartPosition()
        {
            AssertAsync.Run(async () =>
            {
                var bytes = Encoding.ASCII.GetBytes("xxpayload");
                var source = new MemoryStream(bytes, writable: false) { Position = 2 };
                using var request = new UHttpRequest(HttpMethod.POST, new Uri("https://spike.local/upload"));
                request.WithStreamBody(source, leaveOpen: true);

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    ValueTask<RequestBodyReadSession> pending = request.Content.OpenReadSessionAsync(CancellationToken.None);
                    using var session = await pending;
                    var buffer = new byte[16];
                    var read = await session.Stream.ReadAsync(buffer, 0, buffer.Length);
                    Assert.AreEqual("payload", Encoding.ASCII.GetString(buffer, 0, read), "attempt " + attempt);
                }
            });
        }

        [Test]
        public void SingleReaderChannel_PendingReadCycles_SurviveTheValueTaskSourceVersionWrap()
        {
            AssertAsync.Run(async () =>
            {
                // ManualResetValueTaskSourceCore<T>.Version is a short: 131,072 pending-read cycles wrap it twice.
                const int Cycles = 131072;
                var channel = new SingleReaderChannel<int>(capacity: 4);
                for (var i = 0; i < Cycles; i++)
                {
                    ValueTask<int> pending = channel.ReadAsync();
                    if (pending.IsCompleted)
                        Assert.Fail("cycle " + i + ": a read on an empty channel completed early");
                    Assert.IsTrue(channel.TryWrite(i));
                    var value = await pending;
                    if (value != i)
                        Assert.Fail("cycle " + i + ": read " + value);
                }

                channel.Complete();
                Assert.IsFalse(channel.TryRead(out _));
            });
        }

        private static UHttpClient CreateClient(IResponseBodySource body)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new BodyTransport(body),
                DisposeTransport = true,
                FollowRedirects = false
            });
        }

        private sealed class BodyTransport : IHttpTransport
        {
            private readonly IResponseBodySource _body;

            internal BodyTransport(IResponseBodySource body)
            {
                _body = body;
            }

            public Task DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context, CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync((int)HttpStatusCode.OK, new HttpHeaders(), _body, context).AsTask();
            }

            public void Dispose()
            {
            }
        }
    }
}
