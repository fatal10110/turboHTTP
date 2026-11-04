using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// #51: 206 is never stored; Range/If-Range requests bypass the cache.
    /// #52: unsafe-method and revalidation responses stream through the cache without buffering, and the
    /// caller's StreamingResponseRequested flag is left untouched.
    /// </summary>
    public partial class CacheInterceptorTests
    {
        private static readonly TimeSpan s_waitTimeout = TimeSpan.FromSeconds(5);

        [Test]
        public void CacheInterceptor_206Response_IsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Content-Range", "bytes 0-3/10");
                    return RespondBufferedAsync(handler, req, ctx, 206, headers, "part");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/partial"));

                using var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(206, (int)first.StatusCode);
                Assert.AreEqual(2, callCount, "A 206 response must not be stored or served for a later request.");
                Assert.IsNull(second.Headers.Get("X-Cache"));
            });
        }

        [TestCase("Range", "bytes=0-3")]
        [TestCase("If-Range", "\"v1\"")]
        public void CacheInterceptor_RangeRequest_BypassesStoredFullResponse(string headerName, string headerValue)
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                string rangeSeenByTransport = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("ETag", "\"v1\"");
                    if (call == 1)
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "full-body");

                    rangeSeenByTransport = req.Headers.Get(headerName);
                    headers.Set("Content-Range", "bytes 0-3/9");
                    return RespondBufferedAsync(handler, req, ctx, 206, headers, "full");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/range");
                var plain = new UHttpRequest(HttpMethod.GET, uri);
                var ranged = new UHttpRequest(HttpMethod.GET, uri);
                ranged.WithHeader(headerName, headerValue);

                using var first = await pipeline.ExecuteAsync(plain, new RequestContext(plain));
                using var partial = await pipeline.ExecuteAsync(ranged, new RequestContext(ranged));
                using var third = await pipeline.ExecuteAsync(plain, new RequestContext(plain));

                Assert.AreEqual(2, callCount, "The ranged request must reach the origin; the plain one must hit.");
                Assert.AreEqual(headerValue, rangeSeenByTransport);
                Assert.AreEqual(206, (int)partial.StatusCode);
                Assert.IsNull(partial.Headers.Get("X-Cache"));
                Assert.AreEqual("HIT", third.Headers.Get("X-Cache"));
                Assert.AreEqual("full-body", third.GetBodyAsString());
            });
        }

        [Test]
        public void CacheInterceptor_RangeRequestAnsweredWith200_IsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    return RespondBufferedAsync(handler, req, ctx, 200, headers, "whole");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/range-ignored");
                var ranged = new UHttpRequest(HttpMethod.GET, uri);
                ranged.WithHeader("Range", "bytes=0-1");
                var plain = new UHttpRequest(HttpMethod.GET, uri);

                using var first = await pipeline.ExecuteAsync(ranged, new RequestContext(ranged));
                using var second = await pipeline.ExecuteAsync(plain, new RequestContext(plain));

                Assert.AreEqual(2, callCount, "A Range request must not populate the cache.");
                Assert.IsNull(second.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_RevalidationAnswered206_RemovesEntryAndDoesNotStore()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    if (call == 1)
                    {
                        headers.Set("Cache-Control", "no-cache");
                        headers.Set("ETag", "\"v1\"");
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "stored");
                    }

                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Content-Range", "bytes 0-1/6");
                    return RespondBufferedAsync(handler, req, ctx, 206, headers, "st");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/revalidate-206"));

                using var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));
                using var third = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(206, (int)second.StatusCode);
                Assert.AreEqual(3, callCount, "Neither the stale entry nor the 206 may answer the third request.");
                Assert.IsNull(third.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_UnsafePost_StreamsResponseWithoutBuffering_AndInvalidates()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                var uri = new Uri("https://example.test/unsafe-stream");
                int getCount = 0;
                CountingBodySource postBody = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    if (req.Method == HttpMethod.GET)
                    {
                        Interlocked.Increment(ref getCount);
                        headers.Set("Cache-Control", "max-age=60");
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "cached");
                    }

                    postBody = new CountingBodySource("ch0-", "ch1-", "ch2-", "ch3");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(201, headers, postBody, ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var get = new UHttpRequest(HttpMethod.GET, uri);
                using (await pipeline.ExecuteAsync(get, new RequestContext(get)))
                {
                }

                var post = new UHttpRequest(HttpMethod.POST, uri);
                var context = new RequestContext(post);
                context.SetState(TransportBehaviorFlags.StreamingResponseRequested, true);
                var probe = new StreamingProbeHandler(() => postBody.PulledChunks);

                await middleware.Wrap(transport.DispatchAsync)(post, probe, context, CancellationToken.None);

                Assert.AreEqual(201, probe.StatusCode);
                Assert.IsFalse(probe.BodyWasBuffered, "The unsafe-method response must not be buffered by the cache.");
                Assert.AreEqual(1, probe.ChunksPulledAtFirstRead, "The first chunk must reach the caller before the body completes.");
                Assert.AreEqual("ch0-ch1-ch2-ch3", probe.Body);
                Assert.IsTrue(context.GetState(TransportBehaviorFlags.StreamingResponseRequested, false));

                using var afterPost = await pipeline.ExecuteAsync(get, new RequestContext(get));
                Assert.AreEqual(2, getCount, "A 2xx response to POST must invalidate the stored GET response.");
                Assert.IsNull(afterPost.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_UnsafePostErrorStatus_DoesNotInvalidate()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                var uri = new Uri("https://example.test/unsafe-error");
                int getCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    if (req.Method == HttpMethod.GET)
                    {
                        Interlocked.Increment(ref getCount);
                        headers.Set("Cache-Control", "max-age=60");
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "cached");
                    }

                    return RespondBufferedAsync(handler, req, ctx, 404, headers, "missing");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var get = new UHttpRequest(HttpMethod.GET, uri);
                var post = new UHttpRequest(HttpMethod.POST, uri);

                using (await pipeline.ExecuteAsync(get, new RequestContext(get)))
                {
                }

                using var postResponse = await pipeline.ExecuteAsync(post, new RequestContext(post));
                using var afterPost = await pipeline.ExecuteAsync(get, new RequestContext(get));

                Assert.AreEqual(404, (int)postResponse.StatusCode);
                Assert.AreEqual(1, getCount, "RFC 9111 section 4.4 invalidates only on a non-error status.");
                Assert.AreEqual("HIT", afterPost.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_Revalidation200_StreamsWithoutBuffering_AndStoresReplacement()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                CountingBodySource replacement = null;
                string ifNoneMatch = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    if (call == 1)
                    {
                        headers.Set("Cache-Control", "no-cache");
                        headers.Set("ETag", "\"v1\"");
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "old");
                    }

                    ifNoneMatch = req.Headers.Get("If-None-Match");
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("ETag", "\"v2\"");
                    replacement = new CountingBodySource("new0-", "new1-", "new2-", "new3");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(200, headers, replacement, ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/revalidate-stream"));
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                var context = new RequestContext(request);
                context.SetState(TransportBehaviorFlags.StreamingResponseRequested, true);
                var probe = new StreamingProbeHandler(() => replacement.PulledChunks);

                await middleware.Wrap(transport.DispatchAsync)(request, probe, context, CancellationToken.None);

                Assert.AreEqual("\"v1\"", ifNoneMatch);
                Assert.AreEqual(200, probe.StatusCode);
                Assert.AreSame(request, probe.StartedRequest, "The caller sees the original request, not the conditional copy.");
                Assert.AreSame(request, context.Request);
                Assert.IsFalse(probe.BodyWasBuffered, "The revalidation response must not be buffered by the cache.");
                Assert.AreEqual(1, probe.ChunksPulledAtFirstRead, "The first chunk must reach the caller before the body completes.");
                Assert.AreEqual("new0-new1-new2-new3", probe.Body);
                Assert.IsTrue(context.GetState(TransportBehaviorFlags.StreamingResponseRequested, false));

                using var third = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual("HIT", third.Headers.Get("X-Cache"));
                Assert.AreEqual("new0-new1-new2-new3", third.GetBodyAsString());
            });
        }

        [Test]
        public void CacheInterceptor_Revalidation304_ServesStoredEntry_AndLeavesStreamingFlagUnchanged()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "no-cache");
                    headers.Set("ETag", "\"v1\"");
                    return call == 1
                        ? RespondBufferedAsync(handler, req, ctx, 200, headers, "stored-body")
                        : RespondBufferedAsync(handler, req, ctx, 304, headers, string.Empty);
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/revalidate-304-flag"));
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                var context = new RequestContext(request);
                context.SetState(TransportBehaviorFlags.StreamingResponseRequested, true);
                var probe = new StreamingProbeHandler(() => 0);

                await middleware.Wrap(transport.DispatchAsync)(request, probe, context, CancellationToken.None);

                Assert.AreEqual(2, callCount);
                Assert.AreEqual(200, probe.StatusCode);
                Assert.AreEqual(1, probe.RequestStartCount);
                Assert.AreEqual("REVALIDATED", probe.Headers.Get("X-Cache"));
                Assert.AreEqual("stored-body", probe.Body);
                Assert.IsTrue(context.GetState(TransportBehaviorFlags.StreamingResponseRequested, false));
                Assert.AreSame(request, context.Request);
            });
        }

        [Test]
        public void CacheInterceptor_BackgroundRevalidation_StoresStreamedReplacement()
        {
            AssertAsync.Run(async () =>
            {
                var middleware = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                CountingBodySource replacement = null;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"v1\"");
                    if (call == 1)
                    {
                        headers.Set("Cache-Control", "max-age=0, stale-while-revalidate=60");
                        return RespondBufferedAsync(handler, req, ctx, 200, headers, "stale");
                    }

                    headers.Set("Cache-Control", "max-age=60");
                    replacement = new CountingBodySource("fre", "sh");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(200, headers, replacement, ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/background-stream"));
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                using var stale = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("STALE", stale.Headers.Get("X-Cache"));

                await WaitUntilAsync(
                    () => Task.FromResult(replacement != null && replacement.IsDisposed),
                    s_waitTimeout,
                    "Background revalidation did not consume the streamed replacement to its end.");

                using var fresh = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual("HIT", fresh.Headers.Get("X-Cache"));
                Assert.AreEqual("fresh", fresh.GetBodyAsString());
            });
        }

        private static Task RespondBufferedAsync(
            IHttpHandler handler,
            UHttpRequest request,
            RequestContext context,
            int statusCode,
            HttpHeaders headers,
            string body)
        {
            handler.OnRequestStart(request, context);
            return handler.OnResponseStartAsync(
                    statusCode,
                    headers,
                    new BufferedResponseBodySource(Encoding.UTF8.GetBytes(body), HttpHeaders.Empty),
                    context)
                .AsTask();
        }

        /// <summary>Non-buffered body source that counts how many chunks the consumer side has pulled.</summary>
        private sealed class CountingBodySource : IResponseBodySource
        {
            private readonly byte[][] _chunks;
            private int _index;
            private int _offset;
            private int _pulled;
            private int _disposed;

            internal CountingBodySource(params string[] chunks)
            {
                _chunks = new byte[chunks.Length][];
                for (var i = 0; i < chunks.Length; i++)
                    _chunks[i] = Encoding.UTF8.GetBytes(chunks[i]);
            }

            internal int PulledChunks => Volatile.Read(ref _pulled);

            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
                if (_index >= _chunks.Length || destination.IsEmpty)
                    return new ValueTask<int>(0);

                if (_offset == 0)
                    Interlocked.Increment(ref _pulled);

                var chunk = _chunks[_index];
                var count = Math.Min(destination.Length, chunk.Length - _offset);
                chunk.AsSpan(_offset, count).CopyTo(destination.Span);
                _offset += count;
                if (_offset == chunk.Length)
                {
                    _index++;
                    _offset = 0;
                }

                return new ValueTask<int>(count);
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                var buffer = new byte[64];
                while (await ReadAsync(buffer, ct).ConfigureAwait(false) != 0)
                {
                }
            }

            public void Abort()
            {
                Interlocked.Exchange(ref _disposed, 1);
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _disposed, 1);
                return default;
            }
        }

        /// <summary>Caller-side handler: records what the cache delivered and reads the body incrementally.</summary>
        private sealed class StreamingProbeHandler : IHttpHandler
        {
            private readonly Func<int> _pulledChunks;
            private readonly StringBuilder _body = new StringBuilder();

            internal StreamingProbeHandler(Func<int> pulledChunks)
            {
                _pulledChunks = pulledChunks;
            }

            internal int StatusCode { get; private set; }
            internal HttpHeaders Headers { get; private set; }
            internal UHttpRequest StartedRequest { get; private set; }
            internal int RequestStartCount { get; private set; }
            internal bool BodyWasBuffered { get; private set; }
            internal int ChunksPulledAtFirstRead { get; private set; } = -1;
            internal string Body => _body.ToString();

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                StartedRequest = request;
                RequestStartCount++;
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                StatusCode = statusCode;
                Headers = headers;
                BodyWasBuffered = body.TryGetBufferedData(out _);

                var buffer = new byte[256];
                var first = true;
                while (true)
                {
                    var read = await body.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (first)
                    {
                        ChunksPulledAtFirstRead = _pulledChunks();
                        first = false;
                    }

                    if (read == 0)
                        break;

                    _body.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                await body.DisposeAsync().ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Assert.Fail("Unexpected response error: " + error);
            }
        }
    }
}
