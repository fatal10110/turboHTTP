using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.RateLimit;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// #73: a ConcurrencyInterceptor permit covers the request and the response headers. For a
    /// streamed response it is released when the headers are delivered, not when the body ends.
    /// </summary>
    public class ConcurrencyStreamingPermitTests
    {
        [Test]
        public void StreamedResponse_ReleasesPermitAtHeaders_WhileBodyIsStillUnread()
        {
            AssertAsync.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 1);
                var body = new HeldBodySource();
                var options = new UHttpClientOptions
                {
                    Transport = new HeadersOnlyTransport(body),
                    FollowRedirects = false
                };
                options.Interceptors.Add(new ConcurrencyInterceptor(limiter));
                using var client = new UHttpClient(options);

                var response = await client.SendStreamingAsync(
                    new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/stream")));
                try
                {
                    // Body not read yet, but the only per-host and global permit is free again.
                    var probe = limiter.AcquireAsync("example.test");
                    Assert.IsTrue(probe.IsCompleted, "permit is still held by the streamed body");
                    await probe;
                    limiter.Release("example.test");

                    // A second streamed request to the same host is admitted as well.
                    var second = await client.SendStreamingAsync(
                        new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/second")));
                    second.Dispose();
                }
                finally
                {
                    response.Dispose();
                }
            });
        }

        private sealed class HeadersOnlyTransport : IHttpTransport
        {
            private readonly IResponseBodySource _body;

            public HeadersOnlyTransport(IResponseBodySource body)
            {
                _body = body;
            }

            public Task DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context,
                CancellationToken cancellationToken = default)
            {
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync((int)HttpStatusCode.OK, new HttpHeaders(), _body, context).AsTask();
            }

            public ValueTask<UHttpResponse> SendAsync(UHttpRequest request, RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }

        /// <summary>A body that delivers nothing until it is aborted or disposed.</summary>
        private sealed class HeldBodySource : IResponseBodySource
        {
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

            public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                await _release.Task.ConfigureAwait(false);
                return 0;
            }

            public ValueTask DrainAsync(CancellationToken ct) => default;

            public void Abort() => _release.TrySetResult(true);

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct) =>
                new ValueTask<HttpHeaders>(HttpHeaders.Empty);

            public ValueTask DisposeAsync()
            {
                _release.TrySetResult(true);
                return default;
            }
        }
    }
}
