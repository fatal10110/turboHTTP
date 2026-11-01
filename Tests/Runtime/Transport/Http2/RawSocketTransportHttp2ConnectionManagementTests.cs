using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http2;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// RawSocketTransport over pre-pooled HTTP/2 connections (in-memory duplex streams): stream-scoped
    /// failures keep the shared connection (#15), REFUSED_STREAM and GOAWAY-unprocessed requests are
    /// replayed for any method (#16, #17), and the manager drains GOAWAY'd connections (#17).
    /// The origin is 127.0.0.1:1, so a request that wrongly opens a real socket fails at once locally.
    /// </summary>
    [TestFixture]
    public class RawSocketTransportHttp2ConnectionManagementTests
    {
        private const string Host = "127.0.0.1";
        private const int Port = 1;

        [Test]
        public void FiftyConcurrentRequests_OneStreamReset_OtherFortyNineSucceedOnSameConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                int connectionCount = 0;
                harness.AddIdleConnection((codec, ct) =>
                {
                    Interlocked.Increment(ref connectionCount);
                    return ServeAsync(codec, async (c, request) =>
                    {
                        if (request.Path.StartsWith("/fail", StringComparison.Ordinal))
                            await c.WriteFrameAsync(RstStream(request.StreamId, Http2ErrorCode.InternalError), ct);
                        else
                            await RespondAsync(c, request, ct);
                    }, ct);
                }, cts.Token);

                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));

                var tasks = new Task<UHttpResponse>[50];
                for (int i = 0; i < tasks.Length; i++)
                    tasks[i] = harness.SendAsync(HttpMethod.GET, i == 25 ? "/fail" : "/ok/" + i, null, cts.Token);

                int ok = 0;
                for (int i = 0; i < tasks.Length; i++)
                {
                    if (i == 25)
                    {
                        // A stream error affects only its stream (RFC 9113 Section 5.4.2); not replayed.
                        var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(() => tasks[i]);
                        StringAssert.Contains("RST_STREAM", ex.HttpError.Message);
                        continue;
                    }

                    await AssertOkAsync(tasks[i]);
                    ok++;
                }

                Assert.AreEqual(49, ok);
                Assert.IsTrue(harness.Transport.HasHttp2Connection(Host, Port), "the shared connection must survive");
                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/after", null, cts.Token));
                Assert.AreEqual(1, Volatile.Read(ref connectionCount));
            });
        }

        [Test]
        public void HandlerFault_IsNotReplayed_AndConnectionStays()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                harness.AddIdleConnection(
                    (codec, ct) => ServeAsync(codec, (c, request) => RespondAsync(c, request, ct), ct),
                    cts.Token);
                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));

                // A spare connection gives a wrong replay somewhere to go.
                bool spareUsed = false;
                harness.AddIdleConnection((codec, ct) =>
                {
                    spareUsed = true;
                    return ServeAsync(codec, (c, request) => RespondAsync(c, request, ct), ct);
                }, cts.Token);

                var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://{Host}:{Port}/fault"));
                var handler = new ThrowingHandler();
                Exception thrown = null;
                try
                {
                    await harness.Transport.DispatchAsync(request, handler, new RequestContext(request), cts.Token);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                Assert.AreEqual(1, handler.ResponseStartCount, "OnResponseStart must never run twice");
                var surfaced = (object)thrown ?? handler.Error;
                Assert.IsNotNull(surfaced, "the handler fault must surface");
                StringAssert.Contains(ThrowingHandler.Message, surfaced.ToString());

                Assert.IsTrue(harness.Transport.HasHttp2Connection(Host, Port));
                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/after", null, cts.Token));
                Assert.IsFalse(Volatile.Read(ref spareUsed));
            });
        }

        [TestCase(false, TestName = "FailureAfterResponseStart_ConnectionLostDuringUpload_IsNotReplayed")]
        [TestCase(true, TestName = "FailureAfterResponseStart_RefusedStreamDuringUpload_IsNotReplayed")]
        public void FailureAfterResponseStart_DuringUpload_IsNotReplayed(bool refusedStream)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                var handler = new RecordingHandler();

                // SETTINGS_INITIAL_WINDOW_SIZE = 0: the PUT body stays blocked on flow control, so the
                // dispatch is still uploading when the response starts and the failure arrives. PUT with
                // a byte[] body is idempotent and replayable: only "the response started" stops a replay.
                var zeroInitialWindow = new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };
                harness.AddIdleConnection(async (codec, server, ct) =>
                {
                    var decoder = new HpackDecoder();
                    while (true)
                    {
                        var frame = await codec.ReadFrameAsync(16384, ct);
                        if (frame.Type != Http2FrameType.Headers)
                            continue;

                        var observed = new ObservedRequest { StreamId = frame.StreamId };
                        foreach (var (name, value) in decoder.Decode(frame.Payload, 0, frame.Length))
                        {
                            if (name == ":method")
                                observed.Method = value;
                            else if (name == ":path")
                                observed.Path = value;
                        }

                        if (observed.Method != "PUT")
                        {
                            await RespondAsync(codec, observed, ct);
                            continue;
                        }

                        var headerBlock = new HpackEncoder()
                            .Encode(new List<(string, string)> { (":status", "200") })
                            .ToArray();
                        await codec.WriteFrameAsync(new Http2Frame
                        {
                            Type = Http2FrameType.Headers,
                            Flags = Http2FrameFlags.EndHeaders,
                            StreamId = frame.StreamId,
                            Payload = headerBlock,
                            Length = headerBlock.Length
                        }, ct);

                        await handler.ResponseStarted.Task;
                        if (refusedStream)
                            await codec.WriteFrameAsync(RstStream(frame.StreamId, Http2ErrorCode.RefusedStream), ct);
                        else
                            server.Dispose(); // the connection breaks mid-response
                        return;
                    }
                }, zeroInitialWindow, cts.Token);
                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));

                // A spare connection gives a wrong replay somewhere to go.
                bool spareUsed = false;
                harness.AddIdleConnection((codec, ct) =>
                {
                    spareUsed = true;
                    return ServeAsync(codec, (c, request) => RespondAsync(c, request, ct), ct);
                }, cts.Token);

                var put = new UHttpRequest(
                    HttpMethod.PUT,
                    new Uri($"https://{Host}:{Port}/upload"),
                    body: Encoding.ASCII.GetBytes("payload"));
                Exception thrown = null;
                try
                {
                    await harness.Transport.DispatchAsync(put, handler, new RequestContext(put), cts.Token);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                Assert.AreEqual(1, Volatile.Read(ref handler.ResponseStartCount), "OnResponseStart must never run twice");
                Assert.IsFalse(Volatile.Read(ref spareUsed), "a request whose response started must not be replayed");
                Assert.IsNotInstanceOf<Http2RetryableRequestException>(thrown, "no retry marker may escape");
                // After response start the failure belongs to the body (IResponseBodySource); the dispatch
                // may also fail (upload aborted by the lost connection), but never twice.
                Assert.LessOrEqual(
                    (thrown != null ? 1 : 0) + Volatile.Read(ref handler.ErrorCount),
                    1,
                    "at most one dispatch-level error outcome");

                var body = handler.Body;
                Assert.IsNotNull(body);
                await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await body.ReadAsync(new byte[16], cts.Token));
                await body.DisposeAsync();
            });
        }

        [Test]
        public void RefusedStream_PostIsReplayedTransparently()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                int postAttempts = 0;
                harness.AddIdleConnection((codec, ct) => ServeAsync(codec, async (c, request) =>
                {
                    if (request.Method == "POST" && Interlocked.Increment(ref postAttempts) == 1)
                        await c.WriteFrameAsync(RstStream(request.StreamId, Http2ErrorCode.RefusedStream), ct);
                    else
                        await RespondAsync(c, request, ct);
                }, ct), cts.Token);

                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));

                // REFUSED_STREAM: no processing happened (RFC 9113 Section 8.7), so even a POST is replayed.
                using (var response = await harness.SendAsync(HttpMethod.POST, "/submit", "payload", cts.Token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual("POST /submit payload", response.GetBodyAsString());
                }

                Assert.AreEqual(2, Volatile.Read(ref postAttempts));
            });
        }

        [Test]
        public void GoAwayMidBurst_InFlightPosts_AllSucceed()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                const int burst = 4;

                // First connection: completes the warm-up, takes the whole POST burst, then sends
                // GOAWAY(NO_ERROR) with the second POST as last stream and answers only up to it.
                harness.AddIdleConnection(async (codec, ct) =>
                {
                    var decoder = new HpackDecoder();
                    var pending = new List<ObservedRequest>();
                    var partial = new Dictionary<int, ObservedRequest>();
                    while (true)
                    {
                        var request = await ReadRequestAsync(codec, decoder, partial, ct);
                        if (request == null)
                            return;

                        if (request.Method == "GET")
                        {
                            await RespondAsync(codec, request, ct);
                            continue;
                        }

                        pending.Add(request);
                        if (pending.Count < burst)
                            continue;

                        pending.Sort((a, b) => a.StreamId.CompareTo(b.StreamId));
                        int lastStreamId = pending[1].StreamId;
                        await codec.WriteFrameAsync(GoAway(lastStreamId, Http2ErrorCode.NoError), ct);
                        foreach (var p in pending)
                        {
                            if (p.StreamId <= lastStreamId)
                                await RespondAsync(codec, p, ct);
                        }
                    }
                }, cts.Token);

                await AssertOkAsync(harness.SendAsync(HttpMethod.GET, "/warm", null, cts.Token));

                // Replays need a fresh connection; the transport may take either spare.
                for (int i = 0; i < 2; i++)
                {
                    harness.AddIdleConnection(
                        (codec, ct) => ServeAsync(codec, (c, request) => RespondAsync(c, request, ct), ct),
                        cts.Token);
                }

                var tasks = new Task<UHttpResponse>[burst];
                for (int i = 0; i < burst; i++)
                    tasks[i] = harness.SendAsync(HttpMethod.POST, "/post/" + i, "body-" + i, cts.Token);

                for (int i = 0; i < burst; i++)
                {
                    using var response = await tasks[i];
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.AreEqual($"POST /post/{i} body-{i}", response.GetBodyAsString());
                }
            });
        }

        [Test]
        public void Manager_GoAwayWithInFlightStream_EvictsWithoutDisposing_ThenStreamCompletes()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var manager = new Http2ConnectionManager(new Http2Options(), new StreamingOptions());
                var duplex = new TestDuplexStream();
                var handshake = CompleteHandshakeAsync(duplex.ServerStream, null, cts.Token);
                var conn = await manager.GetOrCreateAsync("drain.example.com", 443, duplex.ClientStream, cts.Token);
                var codec = await handshake;

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://drain.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var decoder = new HpackDecoder();
                var observed = await ReadRequestAsync(codec, decoder, new Dictionary<int, ObservedRequest>(), cts.Token);

                await codec.WriteFrameAsync(GoAway(observed.StreamId, Http2ErrorCode.NoError), cts.Token);
                await WaitForConditionAsync(() => !conn.AcceptsNewStreams, cts.Token);

                // Evicted for new requests, but the in-flight stream drains (RFC 9113 Section 6.8).
                Assert.IsNull(manager.GetIfExists("drain.example.com", 443));
                Assert.IsTrue(conn.IsAlive);

                await RespondAsync(codec, observed, cts.Token);
                using (var response = await task)
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                await WaitForConditionAsync(() => !conn.IsAlive, cts.Token);
            });
        }

        [Test]
        public void Manager_Dispose_DisposesDrainingConnection()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var manager = new Http2ConnectionManager(new Http2Options(), new StreamingOptions());
                var duplex = new TestDuplexStream();
                var handshake = CompleteHandshakeAsync(duplex.ServerStream, null, cts.Token);
                var conn = await manager.GetOrCreateAsync("drain.example.com", 443, duplex.ClientStream, cts.Token);
                var codec = await handshake;

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://drain.example.com/a"));
                var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var observed = await ReadRequestAsync(
                    codec,
                    new HpackDecoder(),
                    new Dictionary<int, ObservedRequest>(),
                    cts.Token);

                await codec.WriteFrameAsync(GoAway(observed.StreamId, Http2ErrorCode.NoError), cts.Token);
                await WaitForConditionAsync(() => !conn.AcceptsNewStreams, cts.Token);
                Assert.IsNull(manager.GetIfExists("drain.example.com", 443));
                Assert.IsTrue(conn.IsAlive);

                manager.Dispose();
                Assert.IsFalse(conn.IsAlive, "manager Dispose must close connections that are still draining");
                await TestHelpers.AssertThrowsAsync<UHttpException>(() => task);
            });
        }

        [Test]
        public void ShouldRetryH2Request_AppliesMethodBodyAndAttemptRules()
        {
            var get = new UHttpRequest(HttpMethod.GET, new Uri("https://example.com/"));
            var post = new UHttpRequest(HttpMethod.POST, new Uri("https://example.com/"), body: new byte[] { 1 });
            var notProcessed = new Http2RetryableRequestException(
                new UHttpError(UHttpErrorType.NetworkError, "refused"),
                requestNotProcessed: true);
            var connectionLost = new Http2RetryableRequestException(
                new UHttpError(UHttpErrorType.NetworkError, "lost"),
                requestNotProcessed: false);

            Assert.IsTrue(RawSocketTransport.ShouldRetryH2Request(notProcessed, post, 0, CancellationToken.None));
            Assert.IsTrue(RawSocketTransport.ShouldRetryH2Request(
                notProcessed, post, RawSocketTransport.MaxH2UnprocessedRetries - 1, CancellationToken.None));
            Assert.IsFalse(RawSocketTransport.ShouldRetryH2Request(
                notProcessed, post, RawSocketTransport.MaxH2UnprocessedRetries, CancellationToken.None));

            Assert.IsTrue(RawSocketTransport.ShouldRetryH2Request(connectionLost, get, 0, CancellationToken.None));
            Assert.IsFalse(RawSocketTransport.ShouldRetryH2Request(connectionLost, get, 1, CancellationToken.None));
            Assert.IsFalse(RawSocketTransport.ShouldRetryH2Request(connectionLost, post, 0, CancellationToken.None));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.IsFalse(RawSocketTransport.ShouldRetryH2Request(notProcessed, get, 0, cancelled.Token));
        }

        // --- Harness ---

        private sealed class ObservedRequest
        {
            public int StreamId;
            public string Method;
            public string Path;
            public readonly MemoryStream Body = new MemoryStream();
        }

        private sealed class Harness : IDisposable
        {
            private readonly List<IDisposable> _resources = new List<IDisposable>();
            private readonly List<Task> _servers = new List<Task>();

            public Harness()
            {
                Pool = new TcpConnectionPool(maxConnectionsPerHost: 8, tlsBackend: TlsBackend.SslStream);
                Transport = new RawSocketTransport(Pool, TlsBackend.SslStream);
            }

            public TcpConnectionPool Pool { get; }
            public RawSocketTransport Transport { get; }

            /// <summary>
            /// Queues an idle pooled connection that negotiated h2; <paramref name="serve"/> runs after the
            /// HTTP/2 handshake if the transport ever uses it.
            /// </summary>
            public void AddIdleConnection(Func<Http2FrameCodec, CancellationToken, Task> serve, CancellationToken ct)
            {
                AddIdleConnection((codec, server, token) => serve(codec, token), null, ct);
            }

            /// <summary>
            /// As above; <paramref name="serve"/> also gets the server end (disposing it breaks the
            /// connection) and the server's SETTINGS carry <paramref name="serverSettings"/> (null: none).
            /// </summary>
            public void AddIdleConnection(
                Func<Http2FrameCodec, Stream, CancellationToken, Task> serve,
                byte[] serverSettings,
                CancellationToken ct)
            {
                var pair = LoopbackSocketPair.Create();
                _resources.Add(pair);
                var duplex = new TestDuplexStream();
                var connection = new PooledConnection(pair.ClientSocket, duplex.ClientStream, Host, Port, true);
                var poolKey = TcpConnectionPool.BuildConnectionKey(Host, Port, true);
                connection.UpdateTransportBinding(
                    duplex.ClientStream,
                    Host,
                    Port,
                    isSecure: true,
                    poolKey: poolKey,
                    tlsVersion: "1.3",
                    tlsProviderName: "TestTls",
                    negotiatedAlpnProtocol: "h2");

                _servers.Add(Task.Run(async () =>
                {
                    try
                    {
                        var codec = await CompleteHandshakeAsync(duplex.ServerStream, serverSettings, ct);
                        await serve(codec, duplex.ServerStream, ct);
                    }
                    catch (Exception)
                    {
                        // Unused spare (stream disposed), client closed, or test over.
                    }
                }));

                var idleField = typeof(TcpConnectionPool).GetField(
                    "_idleConnections",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(idleField);
                var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(Pool);
                idle.GetOrAdd(poolKey, _ => new ConcurrentQueue<PooledConnection>()).Enqueue(connection);
            }

            public Task<UHttpResponse> SendAsync(HttpMethod method, string path, string body, CancellationToken ct)
            {
                var request = new UHttpRequest(
                    method,
                    new Uri($"https://{Host}:{Port}{path}"),
                    body: body == null ? null : Encoding.ASCII.GetBytes(body));
                return TransportDispatchHelper.CollectResponseAsync(
                    Transport.DispatchAsync,
                    request,
                    new RequestContext(request),
                    ct);
            }

            public void Dispose()
            {
                Transport.Dispose();
                Pool.Dispose();
                foreach (var resource in _resources)
                    resource.Dispose();
            }
        }

        private sealed class LoopbackSocketPair : IDisposable
        {
            private readonly TcpClient _client;
            private readonly TcpClient _server;

            private LoopbackSocketPair(TcpClient client, TcpClient server)
            {
                _client = client;
                _server = server;
            }

            public Socket ClientSocket => _client.Client;

            public static LoopbackSocketPair Create()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    return new LoopbackSocketPair(client, listener.AcceptTcpClient());
                }
                finally
                {
                    listener.Stop();
                }
            }

            public void Dispose()
            {
                try { _client.Close(); } catch { }
                try { _server.Close(); } catch { }
            }
        }

        /// <summary>Records each callback; keeps the started body for the test (which disposes it).</summary>
        private sealed class RecordingHandler : IHttpHandler
        {
            public readonly TaskCompletionSource<bool> ResponseStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int ResponseStartCount;
            public int ErrorCount;
            public IResponseBodySource Body;

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Interlocked.Increment(ref ResponseStartCount);
                Body = body;
                ResponseStarted.TrySetResult(true);
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Interlocked.Increment(ref ErrorCount);
            }
        }

        private sealed class ThrowingHandler : IHttpHandler
        {
            public const string Message = "handler fault for #15";
            public int ResponseStartCount;
            public UHttpException Error;

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                Interlocked.Increment(ref ResponseStartCount);
                body?.Abort();
                throw new InvalidOperationException(Message);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }

        private static async Task<Http2FrameCodec> CompleteHandshakeAsync(
            Stream serverStream,
            byte[] serverSettings,
            CancellationToken ct)
        {
            var preface = new byte[Http2Constants.ConnectionPreface.Length];
            int read = 0;
            while (read < preface.Length)
            {
                int n = await serverStream.ReadAsync(preface, read, preface.Length - read, ct);
                if (n == 0)
                    throw new IOException("closed before the preface");
                read += n;
            }

            var codec = new Http2FrameCodec(serverStream);
            await codec.ReadFrameAsync(16384, ct);
            var settings = serverSettings ?? Array.Empty<byte>();
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                StreamId = 0,
                Payload = settings,
                Length = settings.Length
            }, ct);
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.Ack,
                StreamId = 0,
                Payload = Array.Empty<byte>(),
                Length = 0
            }, ct);
            await codec.ReadFrameAsync(16384, ct);
            return codec;
        }

        /// <summary>Reads frames until one request is complete (END_STREAM); null once the client closed.</summary>
        private static async Task<ObservedRequest> ReadRequestAsync(
            Http2FrameCodec codec,
            HpackDecoder decoder,
            Dictionary<int, ObservedRequest> partial,
            CancellationToken ct)
        {
            while (true)
            {
                Http2Frame frame;
                try
                {
                    frame = await codec.ReadFrameAsync(16384, ct);
                }
                catch (IOException)
                {
                    return null;
                }

                if (frame.Type == Http2FrameType.GoAway)
                    return null;

                if (frame.Type == Http2FrameType.Headers)
                {
                    var request = new ObservedRequest { StreamId = frame.StreamId };
                    foreach (var (name, value) in decoder.Decode(frame.Payload, 0, frame.Length))
                    {
                        if (name == ":method")
                            request.Method = value;
                        else if (name == ":path")
                            request.Path = value;
                    }

                    if (frame.HasFlag(Http2FrameFlags.EndStream))
                        return request;

                    partial[frame.StreamId] = request;
                }
                else if (frame.Type == Http2FrameType.Data && partial.TryGetValue(frame.StreamId, out var open))
                {
                    open.Body.Write(frame.Payload, 0, frame.Length);
                    if (frame.HasFlag(Http2FrameFlags.EndStream))
                    {
                        partial.Remove(frame.StreamId);
                        return open;
                    }
                }
            }
        }

        private static async Task ServeAsync(
            Http2FrameCodec codec,
            Func<Http2FrameCodec, ObservedRequest, Task> onRequest,
            CancellationToken ct)
        {
            var decoder = new HpackDecoder();
            var partial = new Dictionary<int, ObservedRequest>();
            while (true)
            {
                var request = await ReadRequestAsync(codec, decoder, partial, ct);
                if (request == null)
                    return;

                await onRequest(codec, request);
            }
        }

        /// <summary>200 with body "METHOD path body", so a test can see the replayed request in full.</summary>
        private static async Task RespondAsync(Http2FrameCodec codec, ObservedRequest request, CancellationToken ct)
        {
            var headerBlock = new HpackEncoder().Encode(new List<(string, string)> { (":status", "200") }).ToArray();
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = Http2FrameFlags.EndHeaders,
                StreamId = request.StreamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            }, ct);

            string bodyText = Encoding.ASCII.GetString(request.Body.ToArray());
            var body = Encoding.ASCII.GetBytes(
                bodyText.Length == 0 ? $"{request.Method} {request.Path}" : $"{request.Method} {request.Path} {bodyText}");
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.EndStream,
                StreamId = request.StreamId,
                Payload = body,
                Length = body.Length
            }, ct);
        }

        private static Http2Frame RstStream(int streamId, Http2ErrorCode code)
        {
            uint value = (uint)code;
            return new Http2Frame
            {
                Type = Http2FrameType.RstStream,
                StreamId = streamId,
                Payload = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value },
                Length = 4
            };
        }

        private static Http2Frame GoAway(int lastStreamId, Http2ErrorCode code)
        {
            uint value = (uint)code;
            var payload = new[]
            {
                (byte)((lastStreamId >> 24) & 0x7F), (byte)(lastStreamId >> 16), (byte)(lastStreamId >> 8), (byte)lastStreamId,
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            };
            return new Http2Frame
            {
                Type = Http2FrameType.GoAway,
                StreamId = 0,
                Payload = payload,
                Length = payload.Length
            };
        }

        private static async Task AssertOkAsync(Task<UHttpResponse> task)
        {
            using var response = await task;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // Waits for a state change made by another task; bounded by the test's token, never a fixed sleep.
        private static async Task WaitForConditionAsync(Func<bool> condition, CancellationToken ct)
        {
            while (!condition())
                await Task.Delay(1, ct);
        }
    }
}
