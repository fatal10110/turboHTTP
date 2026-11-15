using System;
using System.Collections.Generic;
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
    /// Bounds of a stacked content-coding chain: every <c>Content-Encoding</c> field line counts (#782), the chain is
    /// capped before any decoder exists (#783), and the input and intermediate work of every layer is bounded and
    /// stops on cancellation or disposal (#774).
    /// </summary>
    public class DecompressionChainBoundsTests
    {
        private const long SmallLimitBytes = 16 * 1024;
        private const int RunawayReadGuard = 10_000;

        private static readonly byte[] Plain = Encoding.UTF8.GetBytes("stacked content codings");

        // ---- #783: chain cap ----

        [TestCase(DecompressionHandler.MaxContentCodings + 1)]
        [TestCase(100)]
        public void ChainOverCap_OneCommaList_FailsWithoutReadingBody(int codings)
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", string.Join(", ", Repeat("gzip", codings)));
                var body = new StreamingBodySource(GzipTimes(Plain, Math.Min(codings, 8)));

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, body);

                AssertChainCapFailure(consumer, body);
            });
        }

        [TestCase(DecompressionHandler.MaxContentCodings + 1)]
        [TestCase(100)]
        public void ChainOverCap_ManyFieldLines_FailsWithoutReadingBody(int codings)
        {
            AssertAsync.Run(async () =>
            {
                // The HTTP/2 shape: one field line per coding (RFC 9110 section 5.3 combines them into one list).
                var headers = new HttpHeaders();
                for (var i = 0; i < codings; i++)
                    headers.Add("Content-Encoding", "gzip");
                var body = new StreamingBodySource(GzipTimes(Plain, Math.Min(codings, 8)));

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, body);

                AssertChainCapFailure(consumer, body);
            });
        }

        [Test]
        public void ChainAtCap_SplitOverFieldLines_WithIdentity_Decodes()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Content-Encoding", "identity, gzip");
                headers.Add("Content-Encoding", "identity");
                headers.Add("Content-Encoding", "gzip, identity, gzip");
                var body = new StreamingBodySource(GzipTimes(Plain, DecompressionHandler.MaxContentCodings));

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, body);

                Assert.IsNull(consumer.Failure, consumer.Failure?.ToString());
                CollectionAssert.AreEqual(Plain, consumer.Body);
                Assert.IsFalse(consumer.Headers.Contains("Content-Encoding"));
            });
        }

        // ---- #782: every field line ----

        [TestCase(true)]
        [TestCase(false)]
        public void TwoGzipFieldLines_DecodeBothLayers(bool buffered)
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Content-Encoding", "gzip");
                headers.Add("Content-Encoding", "gzip");
                var wire = GzipTimes(Plain, 2);
                IResponseBodySource body = buffered ? new MockResponseBodySource(wire) : new StreamingBodySource(wire);

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, body);

                Assert.IsNull(consumer.Failure, consumer.Failure?.ToString());
                CollectionAssert.AreEqual(Plain, consumer.Body);
                Assert.IsFalse(consumer.Headers.Contains("Content-Encoding"));
            });
        }

        [Test]
        public void GzipAndBrFieldLines_PassThroughUndecoded()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Content-Encoding", "gzip");
                headers.Add("Content-Encoding", "br");
                var wire = Encoding.UTF8.GetBytes("brotli bytes this handler cannot decode");

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, new StreamingBodySource(wire));

                Assert.IsNull(consumer.Failure, consumer.Failure?.ToString());
                CollectionAssert.AreEqual(wire, consumer.Body);
                CollectionAssert.AreEqual(new[] { "gzip", "br" }, consumer.Headers.GetValues("Content-Encoding"));
            });
        }

        // ---- #774: work bounds ----

        [TestCase(true)]
        [TestCase(false)]
        public void NestedGzipOverEmptyStoredBlocks_FailsOnIntermediateBudget(bool buffered)
        {
            AssertAsync.Run(async () =>
            {
                // About 1 MB of empty stored blocks (no output at all), gzipped twice into a few KB on the wire.
                var inner = EmptyStoredBlocksGzip(200_000);
                var wire = GzipTimes(inner, 2);
                Assert.Less(wire.Length, SmallLimitBytes, "The wire body must pass the compressed-size limit.");
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip, gzip, gzip");
                IResponseBodySource body = buffered ? new MockResponseBodySource(wire) : new StreamingBodySource(wire);

                var consumer = await DispatchAsync(new DecompressionInterceptor(true, SmallLimitBytes), headers, body);

                AssertDecompressionFailure(consumer.Failure, "intermediate");
                Assert.AreEqual(0, consumer.BodyLength);
            });
        }

        [Test]
        public void UnknownLengthBodyOfEmptyStoredBlocks_FailsOnCompressedBudget()
        {
            AssertAsync.Run(async () =>
            {
                // About 100 KB of wire bytes that decode to nothing, with no Content-Length to reject up front.
                var wire = EmptyStoredBlocksGzip(20_000);
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                var body = new StreamingBodySource(wire, chunkBytes: 4096);

                var consumer = await DispatchAsync(new DecompressionInterceptor(true, SmallLimitBytes), headers, body);

                AssertDecompressionFailure(consumer.Failure, "compressed response body exceeded");
                Assert.Less(body.BytesRead, wire.Length, "The body must stop being read once the budget is spent.");
            });
        }

        // ---- #774: cancellation and disposal ----

        [Test]
        public void LayerRefill_SyncRead_ObservesBudgetCancellation()
        {
            using var cts = new CancellationTokenSource();
            var budget = new DecompressionBudget(long.MaxValue, long.MaxValue, cts.Token);
            var source = new EndlessEmptyBlocksStream(onRead: count =>
            {
                if (count == 4)
                    cts.Cancel();
            });
            using var layer = new InflateLayerStream(source, leaveOpen: true, gzip: true, budget, sourceIsWire: true);

            var failure = Assert.Catch<Exception>(() => layer.Read(new byte[1024], 0, 1024));

            AssertCausedBy<OperationCanceledException>(failure);
            Assert.LessOrEqual(source.ReadCount, 4, "No source read may start after the cancellation.");
        }

        [Test]
        public void BufferedBody_CancelledDispatch_DoesNotDecode()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip");
                var body = new MockResponseBodySource(GzipTimes(Plain, 1));
                var consumer = new Consumer(CancellationToken.None);
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync((int)HttpStatusCode.OK, headers, body, ctx).AsTask();
                });
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/cancelled"));

                Exception failure = null;
                try
                {
                    await new DecompressionInterceptor().Wrap(transport.DispatchAsync)(
                        request,
                        consumer,
                        new RequestContext(request),
                        cts.Token);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                AssertCausedBy<OperationCanceledException>(failure);
                Assert.IsFalse(consumer.Started, "A cancelled dispatch must not deliver a decoded body.");
            });
        }

        [Test]
        public void StreamingBody_InnerReads_ObserveCallerToken()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var headers = new HttpHeaders();
                headers.Set("Content-Encoding", "gzip, gzip");
                var body = new StreamingBodySource(GzipTimes(Plain, 2), chunkBytes: 7);

                var consumer = await DispatchAsync(new DecompressionInterceptor(), headers, body, cts.Token);

                Assert.IsNull(consumer.Failure, consumer.Failure?.ToString());
                CollectionAssert.AreEqual(Plain, consumer.Body);
                Assert.Greater(body.Tokens.Count, 0);
                foreach (var token in body.Tokens)
                    Assert.IsTrue(token.Equals(cts.Token), "A body read did not observe the caller's token.");
            });
        }

        // ---- helpers ----

        private static void AssertChainCapFailure(Consumer consumer, StreamingBodySource body)
        {
            AssertDecompressionFailure(consumer.Failure, "content codings");
            Assert.IsFalse(consumer.Started);
            Assert.AreEqual(0, body.ReadCount, "The body must not be read for an over-cap chain.");
            Assert.AreEqual(1, body.AbortCount);
        }

        private static void AssertDecompressionFailure(Exception failure, string innerMessage)
        {
            Assert.IsNotNull(failure, "Expected the response to fail.");
            Assert.IsInstanceOf<UHttpException>(failure, failure.ToString());
            StringAssert.Contains("decompression", failure.Message.ToLowerInvariant());
            // The platform inflater may wrap an exception thrown by the layer below it.
            for (var current = failure.InnerException; current != null; current = current.InnerException)
            {
                if (current.Message.ToLowerInvariant().Contains(innerMessage))
                    return;
            }

            Assert.Fail($"Expected a cause mentioning '{innerMessage}', got {failure}");
        }

        private static void AssertCausedBy<T>(Exception failure) where T : Exception
        {
            Assert.IsNotNull(failure, "Expected a failure.");
            // The platform inflater may wrap an exception thrown by its input stream.
            for (var current = failure; current != null; current = current.InnerException)
            {
                if (current is T)
                    return;
            }

            Assert.Fail($"Expected {typeof(T).Name}, got {failure}");
        }

        private static async Task<Consumer> DispatchAsync(
            DecompressionInterceptor interceptor,
            HttpHeaders headers,
            IResponseBodySource body,
            CancellationToken readToken = default)
        {
            var transport = new CallbackTransport((req, handler, ctx, ct) =>
            {
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync((int)HttpStatusCode.OK, headers, body, ctx).AsTask();
            });

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/chain"));
            var consumer = new Consumer(readToken);
            await interceptor.Wrap(transport.DispatchAsync)(request, consumer, new RequestContext(request), CancellationToken.None)
                .ConfigureAwait(false);
            return consumer;
        }

        private static IEnumerable<string> Repeat(string value, int count)
        {
            for (var i = 0; i < count; i++)
                yield return value;
        }

        private static byte[] GzipTimes(byte[] input, int times)
        {
            var current = input;
            for (var i = 0; i < times; i++)
            {
                using var stream = new MemoryStream();
                using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
                    gzip.Write(current, 0, current.Length);

                current = stream.ToArray();
            }

            return current;
        }

        /// <summary>One gzip member whose DEFLATE data is <paramref name="blocks"/> empty stored blocks.</summary>
        private static byte[] EmptyStoredBlocksGzip(int blocks)
        {
            using var stream = new MemoryStream();
            stream.Write(GzipHeader, 0, GzipHeader.Length);
            for (var i = 0; i < blocks; i++)
                stream.Write(EmptyStoredBlock, 0, EmptyStoredBlock.Length);

            // Final empty stored block, then CRC32 and ISIZE of the empty output (both zero).
            stream.Write(new byte[] { 0x01, 0x00, 0x00, 0xFF, 0xFF }, 0, 5);
            stream.Write(new byte[8], 0, 8);
            return stream.ToArray();
        }

        private static readonly byte[] GzipHeader = { 0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0, 0x00, 0xFF };

        private static readonly byte[] EmptyStoredBlock = { 0x00, 0x00, 0x00, 0xFF, 0xFF };

        private sealed class RunawayReadException : Exception
        {
            internal RunawayReadException()
                : base("The decoder kept reading a body that decodes to nothing.")
            {
            }
        }

        /// <summary>A gzip header followed by empty stored blocks forever; counts the reads.</summary>
        private sealed class EndlessEmptyBlocksStream : Stream
        {
            private readonly Action<int> _onRead;
            private long _position;

            internal EndlessEmptyBlocksStream(Action<int> onRead)
            {
                _onRead = onRead;
            }

            internal int ReadCount { get; private set; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCount++;
                if (ReadCount > RunawayReadGuard)
                    throw new RunawayReadException();

                for (var i = 0; i < count; i++, _position++)
                {
                    buffer[offset + i] = _position < GzipHeader.Length
                        ? GzipHeader[_position]
                        : EmptyStoredBlock[(_position - GzipHeader.Length) % EmptyStoredBlock.Length];
                }

                _onRead(ReadCount);
                return count;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>A streaming body of unknown length that records its reads, their tokens and aborts.</summary>
        private sealed class StreamingBodySource : IResponseBodySource
        {
            private readonly byte[] _data;
            private readonly int _chunkBytes;
            private int _position;

            internal StreamingBodySource(byte[] data, int chunkBytes = 1024)
            {
                _data = data;
                _chunkBytes = chunkBytes;
            }

            internal int ReadCount { get; private set; }

            internal long BytesRead => _position;

            internal int AbortCount { get; private set; }

            internal List<CancellationToken> Tokens { get; } = new List<CancellationToken>();

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
                ct.ThrowIfCancellationRequested();
                lock (Tokens)
                {
                    ReadCount++;
                    Tokens.Add(ct);
                    var count = Math.Min(destination.Length, Math.Min(_chunkBytes, _data.Length - _position));
                    _data.AsSpan(_position, count).CopyTo(destination.Span);
                    _position += count;
                    return new ValueTask<int>(count);
                }
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                var buffer = new byte[4096];
                while (await ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
                {
                }
            }

            public void Abort()
            {
                AbortCount++;
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class Consumer : IHttpHandler
        {
            private readonly CancellationToken _readToken;

            internal Consumer(CancellationToken readToken)
            {
                _readToken = readToken;
            }

            internal bool Started { get; private set; }

            internal Exception Failure { get; private set; }

            internal HttpHeaders Headers { get; private set; }

            internal byte[] Body { get; private set; }

            internal long BodyLength { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Started = true;
                Headers = headers;
                try
                {
                    using var output = new MemoryStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await body.ReadAsync(buffer, _readToken).ConfigureAwait(false)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        BodyLength += read;
                    }

                    Body = output.ToArray();
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    Failure = ex;
                }
                finally
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Failure = error;
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch;
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _dispatch(request, handler, context, cancellationToken);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
