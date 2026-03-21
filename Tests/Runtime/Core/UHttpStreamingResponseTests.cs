using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class UHttpStreamingResponseTests
    {
        [Test]
        public void ResponseBodyStream_ReadsFromNonBufferedSource_AndReportsKnownLength()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(
                    new ReadOnlyMemory<byte>[]
                    {
                        Encoding.UTF8.GetBytes("pay"),
                        Encoding.UTF8.GetBytes("load")
                    },
                    length: 7);
                Assert.IsFalse(source.TryGetBufferedData(out _));

                await using var response = new UHttpStreamingResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    source);

                Assert.IsTrue(response.Body.CanRead);
                Assert.AreEqual(7, response.Body.Length);

                var buffer = new byte[3];
                using var output = new MemoryStream();
                while (true)
                {
                    var read = await response.Body.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    if (read == 0)
                        break;

                    await output.WriteAsync(buffer, 0, read);
                }

                Assert.AreEqual("payload", Encoding.UTF8.GetString(output.ToArray()));
            });
        }

        [Test]
        public void ResponseBodyStream_Length_ThrowsWhenUnknown()
        {
            using var response = CreateResponse("payload", length: null);

            var ex = Assert.Throws<NotSupportedException>(() => _ = response.Body.Length);
            StringAssert.Contains("not known", ex.Message);
        }

        [Test]
        public void ResponseBodyStream_Dispose_BeforeEndOfBody_AbortsSource_WithoutDisposingResponse()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);

                Assert.AreEqual(3, await response.Body.ReadAsync(new byte[3], 0, 3, CancellationToken.None));

                response.Body.Dispose();

                Assert.AreEqual(1, source.AbortCount);
                Assert.AreEqual(0, source.DisposeAsyncCount);
                Assert.AreEqual(0, releaseCount);
                Assert.IsFalse(response.Body.CanRead);
                AssertAsync.ThrowsAsync<ObjectDisposedException>(
                    async () => await response.Body.ReadAsync(new byte[1], 0, 1, CancellationToken.None));

                AssertAsync.ThrowsAsync<ObjectDisposedException>(async () => await response.GetTrailersAsync());

                await response.DisposeAsync();

                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        [Test]
        public void ResponseBodyStream_DisposeAsync_AbortsBodyWithoutDisposingResponse()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);

                await response.Body.DisposeAsync();

                Assert.AreEqual(1, source.AbortCount);
                Assert.AreEqual(0, source.DisposeAsyncCount);
                Assert.AreEqual(0, releaseCount);
                AssertAsync.ThrowsAsync<ObjectDisposedException>(async () => await response.GetTrailersAsync());

                await response.DisposeAsync();

                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        [Test]
        public void UHttpStreamingResponse_GetTrailersAsync_ReturnsConfiguredTrailers()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "ok");

                await using var response = new UHttpStreamingResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    new MockResponseBodySource(ReadOnlyMemory<byte>.Empty, length: 0, trailers: trailers));

                var result = await response.GetTrailersAsync();

                Assert.AreEqual("ok", result.Get("X-Trailer"));
            });
        }

        [Test]
        public void ResponseBodyStream_Dispose_AfterFullRead_PreservesTrailers_AndResponseLifetime()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Trailer", "ok");

                var source = new MockResponseBodySource(
                    new ReadOnlyMemory<byte>[]
                    {
                        Encoding.UTF8.GetBytes("pay"),
                        Encoding.UTF8.GetBytes("load")
                    },
                    length: 7,
                    trailers: trailers);
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);

                Assert.AreEqual("payload", await ReadAllAsync(response.Body));

                response.Body.Dispose();

                Assert.AreEqual(0, source.AbortCount);
                Assert.AreEqual(0, source.DisposeAsyncCount);
                Assert.AreEqual(0, releaseCount);

                var result = await response.GetTrailersAsync();
                Assert.AreEqual("ok", result.Get("X-Trailer"));

                await response.DisposeAsync();

                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        [Test]
        public void UHttpStreamingResponse_DisposeAsync_DisposesSource_AndInvokesReleaseCallbackOnce()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);
                var response = new UHttpStreamingResponse(HttpStatusCode.OK, new HttpHeaders(), source);
                int releaseCount = 0;
                response.AttachRequestRelease(() => releaseCount++);

                await response.DisposeAsync();
                await response.DisposeAsync();

                Assert.AreEqual(1, source.DisposeAsyncCount);
                Assert.AreEqual(0, source.AbortCount);
                Assert.AreEqual(1, releaseCount);
            });
        }

        [Test]
        public void MockResponseBodySource_DisposeAsync_AfterDetach_DoesNotIncrementDisposeCount()
        {
            AssertAsync.Run(async () =>
            {
                var source = new MockResponseBodySource(Encoding.UTF8.GetBytes("payload"), length: 7);

                Assert.IsTrue(source.TryDetachBufferedBody(out var body));

                await source.DisposeAsync();
                body.DisposeOwnedResources();

                Assert.AreEqual(0, source.DisposeAsyncCount);
            });
        }

        private static UHttpStreamingResponse CreateResponse(string body, long? length)
        {
            if (!length.HasValue)
            {
                return new UHttpStreamingResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    new UnknownLengthBodySource(Encoding.UTF8.GetBytes(body)));
            }

            return new UHttpStreamingResponse(
                HttpStatusCode.OK,
                new HttpHeaders(),
                new MockResponseBodySource(Encoding.UTF8.GetBytes(body), length));
        }

        private static async Task<string> ReadAllAsync(Stream stream)
        {
            var buffer = new byte[4];
            using var output = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }

        private sealed class UnknownLengthBodySource : IResponseBodySource
        {
            private readonly MockResponseBodySource _inner;

            public UnknownLengthBodySource(ReadOnlyMemory<byte> data)
            {
                _inner = new MockResponseBodySource(data, length: data.Length);
            }

            public long? Length => null;

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                return _inner.TryGetBufferedData(out data);
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                return _inner.TryDetachBufferedBody(out body);
            }

            public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                return _inner.ReadAsync(destination, ct);
            }

            public ValueTask DrainAsync(CancellationToken ct)
            {
                return _inner.DrainAsync(ct);
            }

            public void Abort()
            {
                _inner.Abort();
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return _inner.GetTrailersAsync(ct);
            }

            public ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }
        }
    }
}
