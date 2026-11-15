using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Range requests and partial responses are never content-decoded (#784, RFC 9110 sections 8.4, 14.2,
    /// 15.3.7), and a streamed compressed body whose source stalls still honours cancellation, the dispose drain
    /// bound and Abort (#779). On Unity's class library the platform inflater pulls its input through the
    /// synchronous Stream.Read path, so the held-source tests exercise that bridge there.
    /// </summary>
    public class DecompressionRangeAndStallTests
    {
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

        // A gzip member header without optional fields (RFC 1952 section 2.3): magic, CM=deflate, FLG=0,
        // MTIME=0, XFL=0, OS=255. The inflater asks for deflate input right after it.
        private static readonly byte[] GzipHeader = { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff };

        // ---- #784: Range requests and 206 responses ----

        [TestCase("Range", "bytes=0-99")]
        [TestCase("If-Range", "\"v1\"")]
        public void RangeRequest_GetsNoAcceptEncoding(string name, string value)
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, new HttpHeaders(), Encoding.UTF8.GetBytes("plain"));
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set(name, value);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/range"), requestHeaders);

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.IsFalse(
                    transport.LastRequest.Headers.Contains("Accept-Encoding"),
                    $"A request carrying {name} must not ask for a content coding: a range selects coded bytes.");
                Assert.AreEqual(value, transport.LastRequest.Headers.Get(name));
            });
        }

        [Test]
        public void RangeRequest_ExplicitAcceptEncoding_IsKept()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK, new HttpHeaders(), Encoding.UTF8.GetBytes("plain"));
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Range", "bytes=0-99");
                requestHeaders.Set("Accept-Encoding", "identity");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/range"), requestHeaders);

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("identity", transport.LastRequest.Headers.Get("Accept-Encoding"));
            });
        }

        [Test]
        public void PartialContent_GzipSlice_BufferedBody_PassesThroughUndecoded()
        {
            AssertAsync.Run(async () =>
            {
                var full = Gzip(Encoding.UTF8.GetBytes(new string('r', 4096)));
                var slice = new byte[32];
                Array.Copy(full, slice, slice.Length);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Range", $"bytes 0-31/{full.Length}");
                headers.Set("Content-Length", "32");
                var transport = new MockTransport(HttpStatusCode.PartialContent, headers, slice);
                var pipeline = new TestInterceptorPipeline(new[] { new DecompressionInterceptor() }, transport);
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Range", "bytes=0-31");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/range"), requestHeaders);

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
                Assert.AreEqual("gzip", response.Headers.Get("Content-Encoding"));
                Assert.AreEqual($"bytes 0-31/{full.Length}", response.Headers.Get("Content-Range"));
                Assert.AreEqual("32", response.Headers.Get("Content-Length"));
                CollectionAssert.AreEqual(slice, response.Body.ToArray(), "A 206 body is a slice of coded bytes.");
            });
        }

        [Test]
        public void PartialContent_GzipSlice_StreamingBody_PassesThroughUndecoded()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                headers.Set("Content-Range", "bytes 100-109/1000");
                headers.Set("Content-Length", "10");
                var source = new HeldBodySource(GzipHeader);

                var capture = await StartAsync(HttpStatusCode.PartialContent, headers, source);

                Assert.IsNull(capture.Error);
                Assert.AreSame(source, capture.Body, "A 206 body must reach the handler undecoded.");
                Assert.AreSame(headers, capture.Headers);
                Assert.AreEqual("gzip", capture.Headers.Get("Content-Encoding"));
                Assert.AreEqual("bytes 100-109/1000", capture.Headers.Get("Content-Range"));
                Assert.AreEqual("10", capture.Headers.Get("Content-Length"));
                source.Abort();
            });
        }

        // ---- #779: a stalled streamed compressed body ----

        [Test]
        public void StreamedRead_HeldSource_CancelEndsPendingReadWithOperationCanceled()
        {
            AssertAsync.Run(async () =>
            {
                var source = new HeldBodySource(GzipHeader);
                var capture = await StartAsync(HttpStatusCode.OK, GzipHeaders(), source);
                var body = capture.Body;
                Assert.AreNotSame(source, body, "The gzip body must be decoded.");

                using var cts = new CancellationTokenSource();
                var read = body.ReadAsync(new byte[1024], cts.Token).AsTask();
                await WithinGuard(source.ReadHeld.Task, "The read never reached the held source.");
                Assert.IsFalse(read.IsCompleted);

                cts.Cancel();
                Exception failure = null;
                try
                {
                    await WithinGuard(read, "Cancelling the token did not end the pending streamed read.");
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    failure = ex;
                }

                Assert.IsInstanceOf<OperationCanceledException>(
                    failure,
                    $"A cancelled streamed read must end with OperationCanceledException, got {failure}");

                body.Abort();
                Assert.AreEqual(1, source.AbortCount);
            }, Guard + Guard);
        }

        [Test]
        public void DisposeAsync_HeldSource_CompletesWithinDrainBound()
        {
            AssertAsync.Run(async () =>
            {
                var source = new HeldBodySource(GzipHeader);
                var capture = await StartAsync(HttpStatusCode.OK, GzipHeaders(), source);

                await WithinGuard(
                    capture.Body.DisposeAsync().AsTask(),
                    "DisposeAsync must give up its drain of a stalled source after its bound.");

                Assert.IsTrue(source.ReadHeld.Task.IsCompleted, "The drain must have reached the held source.");
                Assert.AreEqual(1, source.AbortCount, "A drain that cannot finish aborts the inner source once.");
            }, Guard + Guard);
        }

        [Test]
        public void Abort_DuringDisposeDrain_AbortsInnerSource()
        {
            AssertAsync.Run(async () =>
            {
                var source = new HeldBodySource(GzipHeader);
                var capture = await StartAsync(HttpStatusCode.OK, GzipHeaders(), source);
                var body = capture.Body;

                var dispose = body.DisposeAsync().AsTask();
                await WithinGuard(source.ReadHeld.Task, "The dispose drain never reached the held source.");

                body.Abort();
                Assert.AreEqual(1, source.AbortCount, "Abort must reach the inner source while DisposeAsync drains.");

                await WithinGuard(dispose, "DisposeAsync must complete once the drain is aborted.");
                Assert.AreEqual(1, source.AbortCount, "The inner source is aborted exactly once.");
                Assert.AreEqual(0, source.DisposeCount, "An aborted drain must not also dispose the inner source.");

                body.Abort();
                Assert.AreEqual(1, source.AbortCount, "Abort after dispose completed is a no-op.");
            }, Guard + Guard);
        }

        private static async Task WithinGuard(Task task, string message)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Guard)).ConfigureAwait(false);
            if (!ReferenceEquals(winner, task))
                Assert.Fail(message);

            await task.ConfigureAwait(false);
        }

        private static HttpHeaders GzipHeaders()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Encoding", "gzip");
            return headers;
        }

        private static byte[] Gzip(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(data, 0, data.Length);

            return output.ToArray();
        }

        private static async Task<CapturingHandler> StartAsync(
            HttpStatusCode status,
            HttpHeaders headers,
            IResponseBodySource source)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/held"));
            var context = new RequestContext(request);
            var capture = new CapturingHandler();
            var handler = new DecompressionHandler(capture, 1024 * 1024, CancellationToken.None);
            handler.OnRequestStart(request, context);
            await handler.OnResponseStartAsync((int)status, headers, source, context).ConfigureAwait(false);
            return capture;
        }

        /// <summary>
        /// A streaming body of unknown length that serves a prefix, then holds every further read until the
        /// read's token is cancelled or the source is aborted, like a stalled or half-open connection.
        /// </summary>
        private sealed class HeldBodySource : IResponseBodySource
        {
            private readonly byte[] _prefix;
            private readonly TaskCompletionSource<bool> _aborted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _served;
            private int _abortCount;
            private int _disposeCount;

            internal HeldBodySource(byte[] prefix)
            {
                _prefix = prefix;
            }

            internal TaskCompletionSource<bool> ReadHeld { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int AbortCount => Volatile.Read(ref _abortCount);

            internal int DisposeCount => Volatile.Read(ref _disposeCount);

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
                ct.ThrowIfCancellationRequested();
                if (_aborted.Task.IsCompleted)
                    throw new IOException("The held source was aborted.");

                if (_served < _prefix.Length)
                {
                    var count = Math.Min(destination.Length, _prefix.Length - _served);
                    _prefix.AsSpan(_served, count).CopyTo(destination.Span);
                    _served += count;
                    return count;
                }

                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetCanceled(ct)))
                {
                    ReadHeld.TrySetResult(true);
                    var winner = await Task.WhenAny(cancelled.Task, _aborted.Task).ConfigureAwait(false);
                    if (ReferenceEquals(winner, _aborted.Task))
                        throw new IOException("The held source was aborted.");

                    await cancelled.Task.ConfigureAwait(false);
                }

                return 0;
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                var buffer = new byte[1024];
                while (await ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
                {
                }
            }

            public void Abort()
            {
                Interlocked.Increment(ref _abortCount);
                _aborted.TrySetResult(true);
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCount);
                return default;
            }
        }

        private sealed class CapturingHandler : IHttpHandler
        {
            internal HttpHeaders Headers { get; private set; }

            internal IResponseBodySource Body { get; private set; }

            internal UHttpException Error { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Headers = headers;
                Body = body;
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }
    }
}
