using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Retry;
using TurboHTTP.Testing;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Phase 22a.6 (#102) extension of the streaming allocation gates: the body a caller reads after the
    /// response went through retry, redirect and a cache replay must cost no more per read than the transport
    /// body itself, and the HTTP/1.1 fast paths without Expect: 100-continue and without trailers (chunked
    /// response read, streamed request upload with known and unknown length) must not allocate per chunk.
    /// </summary>
    public sealed partial class StreamingAllocationGateTests
    {
        private const int PipelineChunkBytes = 1024;
        private const int PipelineChunkCount = 256;
        private const int UploadSendBufferBytes = 4096;
        private const int UploadChunkCount = 512;

        private static readonly byte[] s_pipelinePayload = CreatePatternBytes(PipelineChunkBytes * PipelineChunkCount);
        private static readonly byte[] s_uploadPayload = CreatePatternBytes(UploadSendBufferBytes * UploadChunkCount);
        private static readonly byte[] s_http11ChunkedWire = CreateChunkedWire(s_http11Payload, Http11ChunkBytes);

        [Test]
        public void RetriedStreamingRead_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateRetryTarget);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void RedirectedStreamingRead_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateRedirectTarget);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void CacheReplayStreamingRead_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateCacheReplayTarget);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void Http11ChunkedStreamingReadWithoutTrailers_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateHttp11ChunkedTarget);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void Http11StreamedUploadKnownLengthWithoutExpect_ManagedBytesPerChunk_StaysWithinBudget()
        {
            var measurement = MeasureUpload("http11_streamed_upload_content_length", knownLength: true);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void Http11StreamedUploadChunkedWithoutTrailers_ManagedBytesPerChunk_StaysWithinBudget()
        {
            var measurement = MeasureUpload("http11_streamed_upload_chunked", knownLength: false);
            AssertMeasurementBudget(measurement, preciseCounterBudgetBytesPerRead: 0, fallbackCounterBudgetBytesPerRead: 32);
        }

        private static MeasuredReadTarget CreateRetryTarget()
        {
            var attempts = 0;
            var transport = new CallbackTransport((request, handler, context, cancellationToken) =>
            {
                handler.OnRequestStart(request, context);
                // Odd attempts fail with 503 (retryable), even attempts stream the body.
                if (Interlocked.Increment(ref attempts) % 2 == 1)
                    return StartResponse(handler, context, HttpStatusCode.ServiceUnavailable, new HttpHeaders(), SmallBody());
                return StartResponse(handler, context, HttpStatusCode.OK, new HttpHeaders(), PipelineBody());
            });

            var policy = new RetryPolicy { MaxRetries = 1, InitialDelay = TimeSpan.Zero, UseJitter = false };
            return CreateClientTarget(
                "retry_streaming_body_read",
                transport,
                new RetryInterceptor(policy),
                "https://phase22a.local/retry",
                () => Assert.AreEqual(2, Volatile.Read(ref attempts), "the gate must read the body of the retried attempt"));
        }

        private static MeasuredReadTarget CreateRedirectTarget()
        {
            var hops = 0;
            var transport = new CallbackTransport((request, handler, context, cancellationToken) =>
            {
                handler.OnRequestStart(request, context);
                Interlocked.Increment(ref hops);
                if (request.Uri.AbsolutePath == "/start")
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", "/final");
                    return StartResponse(handler, context, HttpStatusCode.Found, headers, SmallBody());
                }

                return StartResponse(handler, context, HttpStatusCode.OK, new HttpHeaders(), PipelineBody());
            });

            return CreateClientTarget(
                "redirect_streaming_body_read",
                transport,
                new RedirectInterceptor(),
                "https://phase22a.local/start",
                () => Assert.AreEqual(2, Volatile.Read(ref hops), "the gate must read the body of the redirect target"));
        }

        private static MeasuredReadTarget CreateCacheReplayTarget()
        {
            var originHits = 0;
            var transport = new CallbackTransport((request, handler, context, cancellationToken) =>
            {
                handler.OnRequestStart(request, context);
                Interlocked.Increment(ref originHits);
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=3600");
                headers.Set("Content-Length", s_pipelinePayload.Length.ToString());
                return StartResponse(handler, context, HttpStatusCode.OK, headers, PipelineBody());
            });

            var cache = new CacheInterceptor(new CachePolicy
            {
                Storage = new MemoryCacheStorage(),
                MaxCacheableResponseBodyBytes = s_pipelinePayload.Length * 2L
            });

            // Fill the cache once (the tee stores on natural EOF); the measured response is the replay.
            const string url = "https://phase22a.local/cache-replay";
            return CreateClientTarget(
                "cache_replay_streaming_body_read",
                transport,
                cache,
                url,
                () => Assert.AreEqual(1, Volatile.Read(ref originHits), "the gate must read a cache replay, not the origin"),
                prime: client => SendStreamingAndDrain(client.Get(url)));
        }

        private static MeasuredReadTarget CreateClientTarget(
            string name,
            IHttpTransport transport,
            IHttpInterceptor interceptor,
            string url,
            Action assertPath,
            Action<UHttpClient> prime = null)
        {
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                FollowRedirects = interceptor is RedirectInterceptor,
                Interceptors = new List<IHttpInterceptor> { interceptor }
            });

            UHttpStreamingResponse response = null;
            try
            {
                prime?.Invoke(client);
                response = client.Get(url).SendStreamingAsync().GetAwaiter().GetResult();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, name);
                assertPath();
            }
            catch
            {
                response?.Dispose();
                client.Dispose();
                throw;
            }

            var bodySource = ExtractBodySource(response);
            return new MeasuredReadTarget(
                name,
                PipelineChunkBytes,
                buffer => bodySource.ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult(),
                () =>
                {
                    try
                    {
                        response.DisposeAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
        }

        private static Task StartResponse(
            IHttpHandler handler,
            RequestContext context,
            HttpStatusCode status,
            HttpHeaders headers,
            IResponseBodySource body)
        {
            return handler.OnResponseStartAsync((int)status, headers, body, context).AsTask();
        }

        private static MockResponseBodySource SmallBody()
        {
            return new MockResponseBodySource(
                new[] { new ReadOnlyMemory<byte>(s_pipelinePayload, 0, 16) },
                length: 16,
                trailers: HttpHeaders.Empty);
        }

        private static MockResponseBodySource PipelineBody()
        {
            return new MockResponseBodySource(
                ChunkPayload(s_pipelinePayload, PipelineChunkBytes),
                length: s_pipelinePayload.Length,
                trailers: HttpHeaders.Empty,
                exposeBufferedData: false);
        }

        private static MeasuredReadTarget CreateHttp11ChunkedTarget()
        {
            var stream = new DeterministicBodyStream(s_http11ChunkedWire);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = Http11ResponseBodyKind.Chunked
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "phase22a.local", 80, false));
            var source = new Http11ResponseBodySource(head, lease, CancellationToken.None, TimeSpan.FromSeconds(30));

            return new MeasuredReadTarget(
                "http11_chunked_streaming_body_source_read",
                Http11ChunkBytes,
                buffer => source.ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult(),
                () =>
                {
                    try
                    {
                        source.DisposeAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        socket.Dispose();
                    }
                });
        }

        /// <summary>
        /// Managed bytes allocated while <see cref="Http11RequestSerializer.SerializeBodyAsync"/> streams the
        /// upload, divided by the number of body writes. The request, its read session and the warm-up run sit
        /// outside the measured window; the sink and the source complete synchronously, so the whole write runs
        /// on the measuring thread.
        /// </summary>
        private static AllocationMeasurement MeasureUpload(string name, bool knownLength)
        {
            var options = new StreamingOptions { DefaultStreamingSendBufferBytes = UploadSendBufferBytes };
            WriteUpload(knownLength, options, measure: false);

            var (writes, delta) = WriteUpload(knownLength, options, measure: true);
            var measurement = new AllocationMeasurement(
                name,
                s_managedBytesCounter.Name,
                writes,
                delta,
                writes == 0 ? delta : delta / writes);
            TestContext.Progress.WriteLine(
                "[Phase22a.6][Allocation] " + name +
                " counter=" + measurement.CounterName +
                " writes=" + writes +
                " totalBytes=" + delta +
                " bytes/write=" + measurement.BytesPerRead);
            Assert.GreaterOrEqual(writes, UploadChunkCount, name + ": the body was not streamed in send-buffer chunks");
            return measurement;
        }

        private static (int Writes, long Delta) WriteUpload(bool knownLength, StreamingOptions options, bool measure)
        {
            using var request = new UHttpRequest(HttpMethod.POST, new Uri("https://phase22a.local/upload"));
            request.WithStreamBody(
                new DeterministicBodyStream(s_uploadPayload),
                knownLength ? s_uploadPayload.Length : (long?)null);
            var sink = new CountingSinkStream();
            using var session = request.Content.OpenReadSessionAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (measure)
                PrepareForMeasurement();
            var before = s_managedBytesCounter.Read();
            Http11RequestSerializer.SerializeBodyAsync(
                    sink,
                    request.Content,
                    session,
                    CancellationToken.None,
                    writeState: null,
                    streamingOptions: options)
                .GetAwaiter()
                .GetResult();
            var after = s_managedBytesCounter.Read();

            if (knownLength)
            {
                Assert.AreEqual(s_uploadPayload.Length, sink.BytesWritten, "Content-Length upload framing");
            }
            else
            {
                Assert.Greater(sink.BytesWritten, s_uploadPayload.Length, "chunked upload framing");
                Assert.AreEqual("0\r\n\r\n", sink.Tail, "chunked upload must end with the last-chunk and no trailer section");
            }
            return (sink.Writes, Math.Max(0L, after - before));
        }

        private static byte[] CreateChunkedWire(byte[] payload, int chunkSize)
        {
            using var wire = new MemoryStream();
            var size = Encoding.ASCII.GetBytes(chunkSize.ToString("X") + "\r\n");
            var crlf = Encoding.ASCII.GetBytes("\r\n");
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                wire.Write(size, 0, size.Length);
                wire.Write(payload, offset, chunkSize);
                wire.Write(crlf, 0, crlf.Length);
            }

            var last = Encoding.ASCII.GetBytes("0\r\n\r\n");
            wire.Write(last, 0, last.Length);
            return wire.ToArray();
        }

        private sealed class CountingSinkStream : Stream
        {
            internal int Writes { get; private set; }
            internal long BytesWritten { get; private set; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>The last five bytes written (ASCII), enough to see a chunked body's last-chunk.</summary>
            internal string Tail => Encoding.ASCII.GetString(_tail, 0, _tailLength);

            public override void Write(byte[] buffer, int offset, int count) => Record(new ReadOnlySpan<byte>(buffer, offset, count));
            public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Record(new ReadOnlySpan<byte>(buffer, offset, count));
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Record(buffer.Span);
                return default;
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            private readonly byte[] _tail = new byte[5];
            private int _tailLength;

            private void Record(ReadOnlySpan<byte> data)
            {
                Writes++;
                BytesWritten += data.Length;
                foreach (var b in data.Length > _tail.Length ? data.Slice(data.Length - _tail.Length) : data)
                {
                    if (_tailLength == _tail.Length)
                    {
                        Buffer.BlockCopy(_tail, 1, _tail, 0, _tail.Length - 1);
                        _tailLength--;
                    }

                    _tail[_tailLength++] = b;
                }
            }
        }
    }
}
