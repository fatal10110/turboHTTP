using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    public partial class Http2ConnectionTests
    {
        // --- Additional Phase 3B.15 Coverage ---

        [Test]
        public void InitializeAsync_WaitsForSettingsAck()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var conn = new Http2Connection(duplex.ClientStream, "test.example.com", 443);
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var initTask = conn.InitializeAsync(cts.Token);

                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, cts.Token);
                    if (n == 0)
                        throw new IOException("Unexpected end of stream");

                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                var clientAck = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, clientAck.Type);
                Assert.IsTrue(clientAck.HasFlag(Http2FrameFlags.Ack));

                await Task.Delay(200, cts.Token);
                Assert.IsFalse(initTask.IsCompleted);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                await initTask;
                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void SendRequest_HeadersSpanContinuation()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var largeChars = new char[70000];
                for (int i = 0; i < largeChars.Length; i++)
                    largeChars[i] = (char)('!' + (i % 90));

                var largeValue = new string(largeChars);

                var headers = new HttpHeaders();
                headers.Set("x-large-a", largeValue);
                headers.Set("x-large-b", largeValue);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/large-headers"))
                    .WithHeaders(headers);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var first = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, first.Type);
                Assert.IsTrue(first.HasFlag(Http2FrameFlags.EndStream));
                int streamId = first.StreamId;

                bool sawContinuation = false;
                bool endHeaders = first.HasFlag(Http2FrameFlags.EndHeaders);
                while (!endHeaders)
                {
                    var continuation = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    Assert.AreEqual(Http2FrameType.Continuation, continuation.Type);
                    Assert.AreEqual(streamId, continuation.StreamId);
                    sawContinuation = true;
                    endHeaders = continuation.HasFlag(Http2FrameFlags.EndHeaders);
                }

                Assert.IsTrue(sawContinuation, "Expected request headers to span CONTINUATION frames.");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: true),
                    cts.Token);
                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                conn.Dispose();
            });
        }

        [Test]
        public void SendRequest_Cancelled_SendsRstStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var requestCts = new CancellationTokenSource();
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/cancel"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, requestCts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                requestCts.Cancel();
                AssertAsync.ThrowsAsync<OperationCanceledException>(async () => await responseTask);

                var rst = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rst.Type);
                Assert.AreEqual(streamId, rst.StreamId);

                uint errorCode = ((uint)rst.Payload[0] << 24)
                    | ((uint)rst.Payload[1] << 16)
                    | ((uint)rst.Payload[2] << 8)
                    | rst.Payload[3];
                Assert.AreEqual((uint)Http2ErrorCode.Cancel, errorCode);

                conn.Dispose();
            });
        }

        [Test]
        public void Settings_InitialWindowSizeChange_AdjustsStreams()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/window"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                var activeStreamsField = typeof(Http2Connection).GetField(
                    "_activeStreams",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(activeStreamsField);

                var activeStreams =
                    (System.Collections.Concurrent.ConcurrentDictionary<int, Http2Stream>)
                    activeStreamsField.GetValue(conn);
                Assert.IsTrue(activeStreams.TryGetValue(streamId, out var stream));

                int oldWindow = stream.SendWindowSize;
                const uint newInitialWindow = 70000;

                var payload = BuildSettingsPayload(Http2SettingId.InitialWindowSize, newInitialWindow);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = payload.Length
                }, cts.Token);

                var ack = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack.Type);
                Assert.IsTrue(ack.HasFlag(Http2FrameFlags.Ack));

                int expected = oldWindow + ((int)newInitialWindow - Http2Constants.DefaultInitialWindowSize);
                Assert.AreEqual(expected, stream.SendWindowSize);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: true),
                    cts.Token);
                await responseTask;
                conn.Dispose();
            });
        }

        [Test]
        public void Settings_MaxFrameSizeChange_Applied()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                const uint maxFrameSize = 32768;
                var payload = BuildSettingsPayload(Http2SettingId.MaxFrameSize, maxFrameSize);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = payload,
                    Length = payload.Length
                }, cts.Token);

                var ack = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Settings, ack.Type);
                Assert.IsTrue(ack.HasFlag(Http2FrameFlags.Ack));

                var body = new byte[50000];
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://test.example.com/max-frame"),
                    body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var negotiatedMaxFrameSize = (int)maxFrameSize;
                var headers = await serverCodec.ReadFrameAsync(negotiatedMaxFrameSize, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headers.Type);
                int streamId = headers.StreamId;

                var firstData = await serverCodec.ReadFrameAsync(negotiatedMaxFrameSize, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, firstData.Type);
                Assert.AreEqual((int)maxFrameSize, firstData.Length);

                int total = firstData.Length;
                bool endStream = firstData.HasFlag(Http2FrameFlags.EndStream);
                while (!endStream)
                {
                    var frame = await serverCodec.ReadFrameAsync(negotiatedMaxFrameSize, cts.Token);
                    if (frame.Type != Http2FrameType.Data)
                        continue;

                    total += frame.Length;
                    endStream = frame.HasFlag(Http2FrameFlags.EndStream);
                }

                Assert.AreEqual(body.Length, total);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: true),
                    cts.Token);
                await responseTask;
                conn.Dispose();
            });
        }

        [Test]
        public void ContinuationFrame_Unexpected_ProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://test.example.com/unexpected-cont"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);
                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException)
                {
                }
                catch (UHttpException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                conn.Dispose();
            });
        }

        [Test]
        public void HeadersFrame_PaddingLengthExceedsPayload_ProtocolError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://test.example.com/headers-padding"));
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);
                var requestHeaders = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = requestHeaders.StreamId;

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.Padded | Http2FrameFlags.EndHeaders,
                    StreamId = streamId,
                    Payload = new byte[] { 10 },
                    Length = 1
                }, cts.Token);

                var goaway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.GoAway, goaway.Type);
                uint errorCode = ((uint)goaway.Payload[4] << 24)
                    | ((uint)goaway.Payload[5] << 16)
                    | ((uint)goaway.Payload[6] << 8)
                    | goaway.Payload[7];
                Assert.AreEqual((uint)Http2ErrorCode.ProtocolError, errorCode);

                try
                {
                    await responseTask;
                    Assert.Fail("Expected exception");
                }
                catch (Http2ProtocolException)
                {
                }
                catch (UHttpException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                conn.Dispose();
            });
        }

        [Test]
        public void SendRequest_AlreadyCancelled_DoesNotLeakStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                using var alreadyCancelled = new CancellationTokenSource();
                alreadyCancelled.Cancel();

                var (conn, _, _) = await CreateInitializedConnectionAsync(cts.Token);
                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://test.example.com/already-cancelled"));
                var context = new RequestContext(request);

                AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                    await conn.SendRequestAsync(request, context, alreadyCancelled.Token));

                var activeStreamsField = typeof(Http2Connection).GetField(
                    "_activeStreams",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(activeStreamsField);
                var activeStreams =
                    (System.Collections.Concurrent.ConcurrentDictionary<int, Http2Stream>)
                    activeStreamsField.GetValue(conn);
                Assert.AreEqual(0, activeStreams.Count);

                conn.Dispose();
            });
        }

        [Test]
        public void ControlFrameWrites_AcquireWriteLock()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var writeLockField = typeof(Http2Connection).GetField(
                    "_writeLock",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(writeLockField);
                var writeLock = (SemaphoreSlim)writeLockField.GetValue(conn);

                Task<Http2Frame> ackTask;
                await writeLock.WaitAsync(cts.Token);
                try
                {
                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Ping,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                        Length = 8
                    }, cts.Token);

                    ackTask = serverCodec.ReadFrameAsync(16384, cts.Token);
                    var completed = await Task.WhenAny(ackTask, Task.Delay(200, cts.Token));
                    Assert.AreNotSame(
                        ackTask,
                        completed,
                        "Control-frame write should wait until the write lock is released.");
                }
                finally
                {
                    writeLock.Release();
                }

                var ack = await ackTask;
                Assert.AreEqual(Http2FrameType.Ping, ack.Type);
                Assert.IsTrue(ack.HasFlag(Http2FrameFlags.Ack));

                conn.Dispose();
            });
        }

        [Test]
        public void SendDataAsync_ReleasesWriteLockBetweenFrames()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(15000);
                var (conn, serverStream, _) =
                    await CreateInitializedConnectionAsyncWithInitialWindow(1024, cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var body = new byte[2048];
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://test.example.com/release-lock"),
                    body: body);
                var context = new RequestContext(request);
                var responseTask = conn.SendRequestAsync(request, context, cts.Token);

                var headers = await serverCodec.ReadFrameAsync(16384, cts.Token);
                int streamId = headers.StreamId;

                var firstData = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Data, firstData.Type);
                Assert.IsFalse(firstData.HasFlag(Http2FrameFlags.EndStream));
                Assert.LessOrEqual(firstData.Length, 1024);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 },
                    Length = 8
                }, cts.Token);

                var pingAck = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Ping, pingAck.Type);
                Assert.IsTrue(pingAck.HasFlag(Http2FrameFlags.Ack));

                var windowPayload = new byte[] { 0x00, 0x00, 0x10, 0x00 };
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = windowPayload,
                    Length = 4
                }, cts.Token);

                int totalSent = firstData.Length;
                bool endStream = firstData.HasFlag(Http2FrameFlags.EndStream);
                while (!endStream)
                {
                    var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                    if (frame.Type != Http2FrameType.Data)
                        continue;

                    totalSent += frame.Length;
                    endStream = frame.HasFlag(Http2FrameFlags.EndStream);
                }

                Assert.AreEqual(body.Length, totalSent);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(streamId, 200, endStream: true),
                    cts.Token);
                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void FailAllStreams_PreventsNewStreamCreation()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request1 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/fail-all"));
                var ctx1 = new RequestContext(request1);
                var task1 = conn.SendRequestAsync(request1, ctx1, cts.Token);
                var req1Headers = await serverCodec.ReadFrameAsync(16384, cts.Token);

                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Continuation,
                    Flags = Http2FrameFlags.EndHeaders,
                    StreamId = req1Headers.StreamId,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, cts.Token);

                await serverCodec.ReadFrameAsync(16384, cts.Token);

                try
                {
                    await task1;
                    Assert.Fail("Expected exception");
                }
                catch (Exception)
                {
                }

                var request2 = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/new-stream"));
                var ctx2 = new RequestContext(request2);
                AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await conn.SendRequestAsync(request2, ctx2, cts.Token));

                conn.Dispose();
            });
        }

        [Test]
        public void Dispose_DisposesUnderlyingStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var trackedStream = new DisposeTrackingStream(duplex.ClientStream);
                var conn = new Http2Connection(trackedStream, "test.example.com", 443);
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);

                var serverTask = Task.Run(async () =>
                {
                    var preface = new byte[24];
                    int read = 0;
                    while (read < 24)
                    {
                        int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, cts.Token);
                        if (n == 0)
                            throw new IOException("Unexpected end of stream");

                        read += n;
                    }

                    await serverCodec.ReadFrameAsync(16384, cts.Token);

                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Settings,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = Array.Empty<byte>(),
                        Length = 0
                    }, cts.Token);

                    await serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Settings,
                        Flags = Http2FrameFlags.Ack,
                        StreamId = 0,
                        Payload = Array.Empty<byte>(),
                        Length = 0
                    }, cts.Token);

                    await serverCodec.ReadFrameAsync(16384, cts.Token);
                }, cts.Token);

                await conn.InitializeAsync(cts.Token);
                await serverTask;

                conn.Dispose();
                Assert.IsTrue(trackedStream.DisposeCalled);
            });
        }
    }
}
