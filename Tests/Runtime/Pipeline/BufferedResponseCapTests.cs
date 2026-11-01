using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    /// <summary>
    /// #147: the response size cap applies where bytes accumulate in memory (the buffered collector),
    /// is configurable through <see cref="UHttpClientOptions.MaxBufferedResponseBodySize"/>, and does
    /// not apply to streamed bodies (transport tests cover those).
    /// </summary>
    [TestFixture]
    public class BufferedResponseCapTests
    {
        [Test]
        public void Options_DefaultIs100MB_RejectsNegative_AndCloneCopiesValue()
        {
            var options = new UHttpClientOptions();
            Assert.AreEqual(100L * 1024 * 1024, options.MaxBufferedResponseBodySize);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxBufferedResponseBodySize = -1);

            options.MaxBufferedResponseBodySize = 0;
            Assert.AreEqual(0, options.Clone().MaxBufferedResponseBodySize);
            Assert.AreEqual(0, new Http2Options().MaxResponseBodySize, "HTTP/2 has no transport cap by default");
        }

        [Test]
        public void SendBufferedAsync_BodyAboveConfiguredCap_FailsWithNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK, body: new byte[17]);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    MaxBufferedResponseBodySize = 16
                });

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await client.Get("https://example.test/large").SendBufferedAsync();
                });
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                StringAssert.Contains("exceeds maximum size", ex.HttpError.Message);
            });
        }

        [Test]
        public void SendBufferedAsync_BodyAtConfiguredCap_Succeeds()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK, body: new byte[16]);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    MaxBufferedResponseBodySize = 16
                });

                using var response = await client.Get("https://example.test/exact").SendBufferedAsync();
                Assert.AreEqual(16, response.Body.Length);
            });
        }

        [TestCase(10, true)]
        [TestCase(16, false)]
        [TestCase(0, false)]
        public void CollectResponseAsync_ReadLoopCountsBytesAgainstCap(long maxBodyBytes, bool expectFailure)
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/read"));
                var context = new RequestContext(request);
                var body = new ChunkedBodySource(chunkBytes: 4, chunks: 4);

                var responseTask = BufferedDispatchBridge.CollectResponseAsync(
                    async (dispatchRequest, handler, dispatchContext, ct) =>
                    {
                        await handler.OnResponseStartAsync(200, new HttpHeaders(), body, dispatchContext);
                    },
                    request,
                    context,
                    CancellationToken.None,
                    maxBodyBytes);

                if (expectFailure)
                {
                    var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await responseTask);
                    StringAssert.Contains("exceeds maximum size", ex.HttpError.Message);
                    Assert.AreEqual(12, body.BytesRead, "collection stops at the first chunk above the cap");
                }
                else
                {
                    using var response = await responseTask;
                    Assert.AreEqual(16, response.Body.Length);
                }

                Assert.IsTrue(body.Disposed, "the body source is released on every path");
            });
        }

        private sealed class ChunkedBodySource : IResponseBodySource
        {
            private readonly int _chunkBytes;
            private int _chunksLeft;

            public ChunkedBodySource(int chunkBytes, int chunks)
            {
                _chunkBytes = chunkBytes;
                _chunksLeft = chunks;
            }

            public int BytesRead { get; private set; }
            public bool Disposed { get; private set; }
            public long? Length => null;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                body = default;
                return false;
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                if (_chunksLeft == 0)
                    return new ValueTask<int>(0);

                _chunksLeft--;
                int n = Math.Min(_chunkBytes, destination.Length);
                destination.Span.Slice(0, n).Fill((byte)'x');
                BytesRead += n;
                return new ValueTask<int>(n);
            }

            public ValueTask DrainAsync(CancellationToken ct) => default;

            public void Abort() => Disposed = true;

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) =>
                new ValueTask<HttpHeaders>(HttpHeaders.Empty);

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return default;
            }
        }
    }
}
