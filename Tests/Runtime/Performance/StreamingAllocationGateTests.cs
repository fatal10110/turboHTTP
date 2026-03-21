using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Performance
{
    [TestFixture]
    [Category("Benchmark")]
    public sealed class StreamingAllocationGateTests
    {
        private const int Http11ChunkBytes = 1024;
        private const int Http11ChunkCount = 1024;
        private const int Http2ChunkBytes = 1024;
        private const int Http2ChunkCount = 512;
        private const int DecompressionChunkBytes = 4096;

        private static readonly ManagedBytesCounter s_managedBytesCounter = ManagedBytesCounter.Create();
        private static readonly byte[] s_http11Payload = CreatePatternBytes(Http11ChunkBytes * Http11ChunkCount);
        private static readonly byte[] s_http2Chunk = CreatePatternBytes(Http2ChunkBytes);
        private static readonly byte[] s_decompressedPayload = CreatePatternBytes(512 * 1024);
        private static readonly byte[] s_compressedPayload = CompressGzip(s_decompressedPayload);

        [Test]
        public void Http11StreamingRead_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateHttp11Target);
            AssertMeasurementBudget(
                measurement,
                preciseCounterBudgetBytesPerRead: 0,
                fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void Http2StreamingRead_SteadyStateManagedBytesPerRead_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateHttp2Target);
            AssertMeasurementBudget(
                measurement,
                preciseCounterBudgetBytesPerRead: 0,
                fallbackCounterBudgetBytesPerRead: 32);
        }

        [Test]
        public void DecompressionStreamingRead_KnownAllocationPoint_StaysWithinBudget()
        {
            var measurement = MeasureScenario(CreateDecompressionTarget);
            AssertMeasurementBudget(
                measurement,
                preciseCounterBudgetBytesPerRead: 384,
                fallbackCounterBudgetBytesPerRead: 768);
        }

        private static AllocationMeasurement MeasureScenario(Func<MeasuredReadTarget> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            using (var warmup = factory())
            {
                DrainTarget(warmup);
            }

            using var target = factory();

            PrepareForMeasurement();
            var before = s_managedBytesCounter.Read();

            var readCount = DrainTarget(target);

            var after = s_managedBytesCounter.Read();
            var delta = Math.Max(0L, after - before);
            var bytesPerRead = readCount == 0 ? delta : delta / readCount;

            var measurement = new AllocationMeasurement(
                target.Name,
                s_managedBytesCounter.Name,
                readCount,
                delta,
                bytesPerRead);

            TestContext.Progress.WriteLine(
                "[Phase22a.6][Allocation] " + measurement.Name +
                " counter=" + measurement.CounterName +
                " reads=" + measurement.ReadCount +
                " totalBytes=" + measurement.TotalManagedBytes +
                " bytes/read=" + measurement.BytesPerRead);

            return measurement;
        }

        private static int DrainTarget(MeasuredReadTarget target)
        {
            var reads = 0;
            var buffer = new byte[target.BufferSize];
            while (true)
            {
                var read = target.Read(buffer);
                if (read == 0)
                    break;

                reads++;
            }

            return reads;
        }

        private static void AssertMeasurementBudget(
            AllocationMeasurement measurement,
            long preciseCounterBudgetBytesPerRead,
            long fallbackCounterBudgetBytesPerRead)
        {
            if (measurement.ReadCount <= 0)
                Assert.Fail("Allocation scenario '" + measurement.Name + "' did not read any data.");

            var budget = s_managedBytesCounter.IsPrecise
                ? preciseCounterBudgetBytesPerRead
                : fallbackCounterBudgetBytesPerRead;

            Assert.LessOrEqual(
                measurement.BytesPerRead,
                budget,
                "Allocation regression in scenario '" + measurement.Name + "'. " +
                "Counter=" + measurement.CounterName + ", " +
                "reads=" + measurement.ReadCount + ", " +
                "observed=" + measurement.BytesPerRead + " bytes/read, " +
                "budget=" + budget + " bytes/read.");
        }

        private static MeasuredReadTarget CreateHttp11Target()
        {
            var stream = new DeterministicBodyStream(s_http11Payload);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = Http11ResponseBodyKind.ContentLength,
                ContentLength = s_http11Payload.Length
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(
                    socket,
                    stream,
                    "phase22a.local",
                    80,
                    false));

            var source = new Http11ResponseBodySource(
                head,
                lease,
                CancellationToken.None,
                TimeSpan.FromSeconds(30));

            return new MeasuredReadTarget(
                "http11_streaming_body_source_read",
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

        private static MeasuredReadTarget CreateHttp2Target()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://phase22a.local/http2"));
            var context = new RequestContext(request);
            var connection = new Http2Connection(
                Stream.Null,
                "phase22a.local",
                443,
                new Http2Options(),
                new StreamingOptions
                {
                    DefaultHttp2PerStreamReceiveBufferBytes = Http2ChunkBytes * Http2ChunkCount
                });
            var stream = new Http2Stream();
            stream.Initialize(
                1,
                request,
                NoOpHttpHandler.Instance,
                context,
                65535,
                65535,
                connection);

            var source = new Http2ResponseBodySource(
                connection,
                stream,
                Http2ChunkBytes * (long)Http2ChunkCount,
                completed: false);

            for (var i = 0; i < Http2ChunkCount; i++)
            {
                Assert.AreEqual(
                    Http2ResponseBodyEnqueueResult.Accepted,
                    source.TryEnqueueData(
                        s_http2Chunk,
                        0,
                        s_http2Chunk.Length,
                        flowControlledLength: 0));
            }

            source.Complete();

            return new MeasuredReadTarget(
                "http2_streaming_body_source_read",
                Http2ChunkBytes,
                buffer => source.ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult(),
                () =>
                {
                    try
                    {
                        source.DisposeAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        request.Dispose();
                        connection.Dispose();
                    }
                });
        }

        private static MeasuredReadTarget CreateDecompressionTarget()
        {
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new CallbackTransport(
                    (request, handler, context, cancellationToken) =>
                    {
                        handler.OnRequestStart(request, context);

                        var headers = new HttpHeaders();
                        headers.Set("Content-Encoding", "gzip");
                        headers.Set("Content-Length", s_compressedPayload.Length.ToString());

                        return handler.OnResponseStartAsync(
                                (int)HttpStatusCode.OK,
                                headers,
                                new MockResponseBodySource(
                                    ChunkPayload(s_compressedPayload, 512),
                                    length: s_compressedPayload.Length,
                                    trailers: HttpHeaders.Empty,
                                    exposeBufferedData: false),
                                context)
                            .AsTask();
                    }),
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor>
                {
                    new DecompressionInterceptor()
                }
            });

            var response = client.Get("https://phase22a.local/gzip")
                .SendStreamingAsync()
                .GetAwaiter()
                .GetResult();

            var bodySource = ExtractBodySource(response);

            return new MeasuredReadTarget(
                "decompression_streaming_body_source_read",
                DecompressionChunkBytes,
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

        private static IResponseBodySource ExtractBodySource(UHttpStreamingResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));
            var bodySource = response.BodySourceForTesting;
            if (bodySource == null)
                throw new InvalidOperationException("Streaming response body source was null.");

            return bodySource;
        }

        private static IEnumerable<ReadOnlyMemory<byte>> ChunkPayload(byte[] bytes, int chunkSize)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                yield return new ReadOnlyMemory<byte>(bytes, offset, count);
            }
        }

        private static byte[] CreatePatternBytes(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((i * 31) % 251);

            return bytes;
        }

        private static byte[] CompressGzip(byte[] payload)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(payload, 0, payload.Length);
            }

            return output.ToArray();
        }

        private static void PrepareForMeasurement()
        {
            if (!s_managedBytesCounter.IsPrecise)
                ForceGc();
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private readonly struct AllocationMeasurement
        {
            internal AllocationMeasurement(
                string name,
                string counterName,
                int readCount,
                long totalManagedBytes,
                long bytesPerRead)
            {
                Name = name;
                CounterName = counterName;
                ReadCount = readCount;
                TotalManagedBytes = totalManagedBytes;
                BytesPerRead = bytesPerRead;
            }

            internal string Name { get; }
            internal string CounterName { get; }
            internal int ReadCount { get; }
            internal long TotalManagedBytes { get; }
            internal long BytesPerRead { get; }
        }

        private sealed class MeasuredReadTarget : IDisposable
        {
            private readonly Func<Memory<byte>, int> _read;
            private readonly Action _dispose;
            private int _disposed;

            internal MeasuredReadTarget(
                string name,
                int bufferSize,
                Func<Memory<byte>, int> read,
                Action dispose)
            {
                Name = name ?? throw new ArgumentNullException(nameof(name));
                BufferSize = bufferSize > 0
                    ? bufferSize
                    : throw new ArgumentOutOfRangeException(nameof(bufferSize));
                _read = read ?? throw new ArgumentNullException(nameof(read));
                _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
            }

            internal string Name { get; }
            internal int BufferSize { get; }

            internal int Read(Memory<byte> buffer)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(MeasuredReadTarget));

                return _read(buffer);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _dispose();
            }
        }

        private sealed class ManagedBytesCounter
        {
            private readonly Func<long> _read;

            private ManagedBytesCounter(string name, Func<long> read, bool isPrecise)
            {
                Name = name;
                _read = read ?? throw new ArgumentNullException(nameof(read));
                IsPrecise = isPrecise;
            }

            internal string Name { get; }
            internal bool IsPrecise { get; }

            internal long Read()
            {
                return _read();
            }

            internal static ManagedBytesCounter Create()
            {
                var api = typeof(GC).GetMethod(
                    "GetAllocatedBytesForCurrentThread",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                if (api != null && api.ReturnType == typeof(long))
                {
                    var read = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), api);
                    return new ManagedBytesCounter(
                        "GC.GetAllocatedBytesForCurrentThread",
                        read,
                        isPrecise: true);
                }

                return new ManagedBytesCounter(
                    "GC.GetTotalMemory(forceFullCollection: true)",
                    () => GC.GetTotalMemory(forceFullCollection: true),
                    isPrecise: false);
            }
        }

        private sealed class DeterministicBodyStream : Stream
        {
            private readonly byte[] _payload;
            private int _offset;
            private bool _disposed;

            internal DeterministicBodyStream(byte[] payload)
            {
                _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            }

            public override bool CanRead => !_disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;
            public override long Position
            {
                get => _offset;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                    throw new ArgumentNullException(nameof(buffer));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DeterministicBodyStream));

                return ReadCore(buffer.AsMemory(offset, count).Span);
            }

            public override int Read(Span<byte> buffer)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DeterministicBodyStream));

                return ReadCore(buffer);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(ReadCore(buffer.Span));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush()
            {
            }

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                base.Dispose(disposing);
            }

            private int ReadCore(Span<byte> destination)
            {
                if (_offset >= _payload.Length)
                    return 0;

                var count = Math.Min(destination.Length, _payload.Length - _offset);
                new ReadOnlySpan<byte>(_payload, _offset, count).CopyTo(destination);
                _offset += count;
                return count;
            }
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
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
