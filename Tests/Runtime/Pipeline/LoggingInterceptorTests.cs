using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Observability;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class LoggingInterceptorTests
    {
        [Test]
        public void LogsRequestAndResponse()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(msg => logs.Add(msg));
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/api"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(3, logs.Count);
                Assert.That(logs[0], Does.Contain("GET"));
                Assert.That(logs[0], Does.Contain("test.com"));
                Assert.That(logs[1], Does.Contain("200"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void LogLevelNone_NoLogs()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(
                    msg => logs.Add(msg),
                    LoggingInterceptor.LogLevel.None);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.IsEmpty(logs);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NonSuccessStatus_LogsWarn()        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(msg => logs.Add(msg));
                var transport = new MockTransport(HttpStatusCode.NotFound);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.That(logs, Has.Some.Contains("[WARN]"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Exception_LogsError()
        {
            var logs = new List<string>();
            var middleware = new LoggingInterceptor(msg => logs.Add(msg));
            var transport = new MockTransport((req, ctx, ct) =>
            {
                throw new UHttpException(
                    new UHttpError(UHttpErrorType.NetworkError, "Connection refused"));
            });
            var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            AssertAsync.ThrowsAsync<UHttpException, UHttpResponse>(
                () => pipeline.ExecuteAsync(request, context));

            Assert.AreEqual(2, logs.Count); // Request log + error log
            Assert.That(logs[1], Does.Contain("[ERROR]"));
        }

        [Test]
        public void DetailedLogging_StreamingRequestBody_LogsMetadataWithoutBuffering()
        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(
                    msg => logs.Add(msg),
                    LoggingInterceptor.LogLevel.Detailed,
                    logHeaders: false,
                    logBody: true);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.com/upload"))
                    .WithStreamBody(
                        new NonSeekableStream(Encoding.UTF8.GetBytes("stream-body")),
                        contentLength: 11,
                        leaveOpen: false);

                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.That(logs[0], Does.Contain("streaming body preview unavailable without buffering"));
                Assert.That(logs[0], Does.Contain("length=11"));
                Assert.That(logs[0], Does.Contain("replayability=NonReplayable"));
                Assert.That(logs[0], Does.Not.Contain("stream-body"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DetailedLogging_StreamingResponse_CompletesOnDispose()
        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(
                    msg => logs.Add(msg),
                    LoggingInterceptor.LogLevel.Detailed,
                    logHeaders: false,
                    logBody: true);
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new MockResponseBodySource(
                            new[]
                            {
                                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("hel"),
                                Encoding.UTF8.GetBytes("lo")
                            },
                            length: 5,
                            trailers: HttpHeaders.Empty,
                            exposeBufferedData: false),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/stream"));
                var context = new RequestContext(request);

                var response = await StreamingDispatchBridge.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                try
                {
                    Assert.AreEqual(2, logs.Count);

                    using var reader = new StreamReader(response.Body, Encoding.UTF8, false, 1024, leaveOpen: true);
                    Assert.AreEqual("hello", await reader.ReadToEndAsync());
                    Assert.AreEqual(2, logs.Count);
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(3, logs.Count);
                Assert.That(logs[2], Does.Contain("completed in"));
                Assert.That(logs[2], Does.Contain("(5 bytes)"));
                Assert.That(logs[2], Does.Contain("hello"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DetailedLogging_StreamingResponseReadFailure_LogsErrorOnDispose()
        {
            Task.Run(async () =>
            {
                var logs = new List<string>();
                var middleware = new LoggingInterceptor(
                    msg => logs.Add(msg),
                    LoggingInterceptor.LogLevel.Detailed,
                    logHeaders: false,
                    logBody: true);
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new FaultingBodySource(
                            Encoding.UTF8.GetBytes("abc"),
                            new IOException("stream broke")),
                        ctx).AsTask();
                });

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com/fault"));
                var context = new RequestContext(request);

                var response = await StreamingDispatchBridge.CollectResponseAsync(
                    pipeline.Pipeline,
                    request,
                    context,
                    CancellationToken.None);

                try
                {
                    var buffer = new byte[3];
                    Assert.AreEqual(3, await response.Body.ReadAsync(buffer, CancellationToken.None));
                    var ex = Assert.ThrowsAsync<IOException>(async () =>
                    {
                        await response.Body.ReadAsync(new byte[1], 0, 1, CancellationToken.None);
                    });
                    Assert.That(ex.Message, Does.Contain("stream broke"));
                    Assert.AreEqual(2, logs.Count);
                }
                finally
                {
                    await response.DisposeAsync();
                }

                Assert.AreEqual(3, logs.Count);
                Assert.That(logs[2], Does.Contain("[ERROR]"));
                Assert.That(logs[2], Does.Contain("stream broke"));
                Assert.That(logs[2], Does.Not.Contain("completed in"));
            }).GetAwaiter().GetResult();
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

        private sealed class FaultingBodySource : IResponseBodySource
        {
            private readonly ReadOnlyMemory<byte> _firstChunk;
            private readonly Exception _failure;
            private int _readCount;

            internal FaultingBodySource(ReadOnlyMemory<byte> firstChunk, Exception failure)
            {
                _firstChunk = firstChunk;
                _failure = failure ?? throw new ArgumentNullException(nameof(failure));
            }

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

                if (Interlocked.Increment(ref _readCount) == 1)
                {
                    _firstChunk.CopyTo(destination);
                    return new ValueTask<int>(_firstChunk.Length);
                }

                throw _failure;
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                byte[] buffer = new byte[16];
                while (await ReadAsync(buffer, ct).ConfigureAwait(false) != 0)
                {
                }
            }

            public void Abort()
            {
            }

            public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return new ValueTask<HttpHeaders>(HttpHeaders.Empty);
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            internal NonSeekableStream(byte[] buffer)
                : base(buffer, writable: false)
            {
            }

            public override bool CanSeek => false;
        }
    }
}
