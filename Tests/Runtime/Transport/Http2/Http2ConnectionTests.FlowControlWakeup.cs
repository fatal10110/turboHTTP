using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// HTTP/2 flow control (RFC 9113 Section 6.9): blocked senders are woken by every event that can
    /// let them continue, windows may go negative after an INITIAL_WINDOW_SIZE decrease, the local
    /// receive window is credited before WINDOW_UPDATE is written, and a stream window overflow is a
    /// stream error.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        // --- #18: wake-ups ---

        [Test]
        public void FlowControl_TwoBlockedUploads_InterleavedStreamWindowUpdates_BothComplete()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Every stream starts at 0: both uploads block on their own stream window.
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                const int bodyLength = 2048;
                var taskA = SendPostAsync(conn, "/a", bodyLength, cts.Token);
                var headersA = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                var taskB = SendPostAsync(conn, "/b", bodyLength, cts.Token);
                var headersB = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // Credit for the stream that blocked second must wake that stream, not the first waiter.
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(headersB.StreamId, bodyLength), cts.Token);
                var receivedB = await ReadUploadsToEndAsync(serverCodec, new[] { headersB.StreamId }, cts.Token);
                Assert.AreEqual(bodyLength, receivedB[headersB.StreamId]);

                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(headersA.StreamId, bodyLength), cts.Token);
                var receivedA = await ReadUploadsToEndAsync(serverCodec, new[] { headersA.StreamId }, cts.Token);
                Assert.AreEqual(bodyLength, receivedA[headersA.StreamId]);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersA.StreamId, 200, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersB.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(taskA, cts.Token)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(taskB, cts.Token)).StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void FlowControl_TwoUploadsBlockedOnConnectionWindow_OneConnectionWindowUpdateWakesBoth()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // Stream windows are open (65535); the connection window is exhausted.
                SetConnectionSendWindow(conn, 0);

                const int bodyLength = 2048;
                var taskA = SendPostAsync(conn, "/a", bodyLength, cts.Token);
                var headersA = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                var taskB = SendPostAsync(conn, "/b", bodyLength, cts.Token);
                var headersB = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // One connection WINDOW_UPDATE covers both bodies, so it must wake both senders.
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(0, 2 * bodyLength), cts.Token);

                var received = await ReadUploadsToEndAsync(
                    serverCodec, new[] { headersA.StreamId, headersB.StreamId }, cts.Token);
                Assert.AreEqual(bodyLength, received[headersA.StreamId]);
                Assert.AreEqual(bodyLength, received[headersB.StreamId]);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersA.StreamId, 200, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersB.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(taskA, cts.Token)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(taskB, cts.Token)).StatusCode);
                Assert.AreEqual(0, ReadConnectionSendWindow(conn));

                conn.Dispose();
            });
        }

        [Test]
        public void FlowControl_EarlyFinalResponseAndRstNoError_UnblocksUploadWithResponse()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/too-large"))
                    .WithBody(new byte[4096]);
                var handler = new RecordingHandler();
                var dispatch = conn.DispatchAsync(request, handler, new RequestContext(request), cts.Token);

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 8.1: the server answers before the body is done and resets the
                // stream with NO_ERROR. The uploader, blocked on a zero window, must stop at once.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 413, endStream: true), cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildRstStreamFrame(headers.StreamId, Http2ErrorCode.NoError), cts.Token);

                await WithWatchdog(dispatch, cts.Token);
                Assert.AreEqual(413, handler.StatusCode);
                Assert.IsNull(handler.Error);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void FlowControl_RstStreamDuringBlockedUpload_FailsRequestPromptly()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/reset"))
                    .WithBody(new byte[4096]);
                var handler = new RecordingHandler();
                var dispatch = conn.DispatchAsync(request, handler, new RequestContext(request), cts.Token);

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                await serverCodec.WriteFrameAsync(
                    BuildRstStreamFrame(headers.StreamId, Http2ErrorCode.Cancel), cts.Token);

                await WithWatchdog(dispatch, cts.Token);
                Assert.IsNotNull(handler.Error, "the reset must reach the caller as an error");
                StringAssert.Contains("RST_STREAM", handler.Error.HttpError.Message);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void FlowControl_InitialWindowSizeIncrease_WakesBlockedUpload()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                const int bodyLength = 1024;
                var task = SendPostAsync(conn, "/grow", bodyLength, cts.Token);
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 6.9.2: the positive delta applies to the open stream; no WINDOW_UPDATE.
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = BuildSettingsPayload(Http2SettingId.InitialWindowSize, 65535),
                    Length = 6
                }, cts.Token);

                var received = await ReadUploadsToEndAsync(serverCodec, new[] { headers.StreamId }, cts.Token);
                Assert.AreEqual(bodyLength, received[headers.StreamId]);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(task, cts.Token)).StatusCode);

                conn.Dispose();
            });
        }

        // --- #23: INITIAL_WINDOW_SIZE decrease ---

        [Test]
        public void FlowControl_InitialWindowSizeDecreaseMidUpload_WindowGoesNegative_UploadCompletes()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 1024), cts.Token);

                const int bodyLength = 4096;
                var task = SendPostAsync(conn, "/shrink", bodyLength, cts.Token);
                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                int streamId = headers.StreamId;

                // The first 1024 bytes use the whole stream window.
                var first = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Data, cts.Token);
                Assert.AreEqual(1024, first.Length);
                Assert.IsFalse(first.HasFlag(Http2FrameFlags.EndStream));

                // RFC 9113 Section 6.9.2: 1024 -> 0 makes the stream window -1024. That is legal: the
                // client acknowledges the SETTINGS and waits; it must not fail the connection.
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);
                Assert.IsTrue(conn.IsAlive);
                Assert.IsTrue(GetActiveStreams(conn).TryGetValue(streamId, out var stream));
                Assert.AreEqual(-1024, stream.SendWindowSize);

                // Back to 0 is still no credit; 0 + 4096 is enough for the rest of the body.
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(streamId, 1024), cts.Token);
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(streamId, bodyLength), cts.Token);

                var received = await ReadUploadsToEndAsync(serverCodec, new[] { streamId }, cts.Token);
                Assert.AreEqual(bodyLength - 1024, received[streamId]);

                await serverCodec.WriteFrameAsync(BuildResponseHeadersFrame(streamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(task, cts.Token)).StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        [Test]
        public void FlowControl_StreamsCreatedWhileInitialWindowSizeChanges_GetTheCurrentWindow()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);
                var activeStreams = GetActiveStreams(conn);

                // A stream registered before the SETTINGS is applied gets the delta; one registered after
                // snapshots the new value. Either way its window equals the new INITIAL_WINDOW_SIZE: never
                // the old value, never the delta applied twice.
                for (int i = 0; i < 40; i++)
                {
                    uint newInitialWindow = (uint)(i % 2 == 0 ? 1000 : 3000);
                    var settingsWrite = serverCodec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Settings,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = BuildSettingsPayload(Http2SettingId.InitialWindowSize, newInitialWindow),
                        Length = 6
                    }, cts.Token);
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/race" + i));
                    var task = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                    await settingsWrite;

                    Http2Frame headers = null;
                    bool acked = false;
                    while (headers == null || !acked)
                    {
                        var frame = await ReadFrameMatchingAsync(
                            serverCodec,
                            f => f.Type == Http2FrameType.Headers ||
                                 (f.Type == Http2FrameType.Settings && f.HasFlag(Http2FrameFlags.Ack)),
                            cts.Token);
                        if (frame.Type == Http2FrameType.Headers)
                            headers = frame;
                        else
                            acked = true;
                    }

                    Assert.IsTrue(activeStreams.TryGetValue(headers.StreamId, out var stream));
                    Assert.AreEqual((int)newInitialWindow, stream.SendWindowSize, $"iteration {i}");

                    await serverCodec.WriteFrameAsync(
                        BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);
                    Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(task, cts.Token)).StatusCode);
                }

                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- #24: credit before WINDOW_UPDATE ---

        [Test]
        public void FlowControl_PeerUsesStreamCreditBeforeWindowUpdateWriteReturns_NoFlowControlError()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var duplex = new TestDuplexStream();
                var clientStream = new PostWriteHoldStream(duplex.ClientStream);
                var conn = new Http2Connection(clientStream, "test.example.com", 443);
                var serverCodec = new Http2FrameCodec(duplex.ServerStream);
                try
                {
                    await PeerUsesCreditBeforeWindowUpdateWriteReturnsAsync(conn, clientStream, serverCodec, duplex, cts.Token);
                }
                finally
                {
                    // A failed run must not leave the held write (and with it _writeLock) blocked.
                    clientStream.Release();
                    conn.Dispose();
                }
            });
        }

        private async Task PeerUsesCreditBeforeWindowUpdateWriteReturnsAsync(
            Http2Connection conn,
            PostWriteHoldStream clientStream,
            Http2FrameCodec serverCodec,
            TestDuplexStream duplex,
            CancellationToken ct)
        {
            const int frameHeaderSize = 9;
            await InitializeWithEmptySettingsAsync(conn, duplex, serverCodec, ct);
            long clientBytesRead = 2 * frameHeaderSize; // server SETTINGS + SETTINGS ACK

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/download"));
            var responseTask = conn.SendStreamingRequestAsync(request, new RequestContext(request), ct)
                .AsTask();
            var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, ct);
            int streamId = headers.StreamId;

            var responseHeaders = BuildResponseHeadersFrame(streamId, 200);
            clientBytesRead += frameHeaderSize + responseHeaders.Length;
            await serverCodec.WriteFrameAsync(responseHeaders, ct);
            // Not disposed here: on failure that would wait for the held write. conn.Dispose() ends it.
            var response = await WithWatchdogResult(responseTask, ct);

            // Use the whole stream receive window; the streaming consumer has not read anything.
            Assert.IsTrue(GetActiveStreams(conn).TryGetValue(streamId, out var stream));
            int remaining = stream.RecvWindowSize;
            var processed = clientStream.WaitForReadStartedAfter(
                clientBytesRead += remaining + frameHeaderSize * ((remaining + 16383) / 16384));
            while (remaining > 0)
            {
                int length = Math.Min(16384, remaining);
                await serverCodec.WriteFrameAsync(BuildDataFrame(streamId, length), ct);
                remaining -= length;
            }

            await WithWatchdog(processed, ct);
            Assert.AreEqual(0, stream.RecvWindowSize);

            // The consumer reads one 16 KiB chunk. Its stream WINDOW_UPDATE reaches the peer, but the
            // write call has not returned yet when the peer already spends the credit.
            var held = clientStream.HoldAfterWindowUpdate(streamId);
            var read = response.Body.ReadAsync(new byte[16384], ct).AsTask();
            await WithWatchdog(held, ct);

            var update = await ReadFrameMatchingAsync(
                serverCodec, f => f.Type == Http2FrameType.WindowUpdate && f.StreamId == streamId, ct);
            Assert.AreEqual(16384, ReadWindowIncrement(update));

            processed = clientStream.WaitForReadStartedAfter(clientBytesRead += frameHeaderSize + 16384);
            await serverCodec.WriteFrameAsync(BuildDataFrame(streamId, 16384), ct);
            await WithWatchdog(processed, ct);

            Assert.IsTrue(conn.IsAlive, "DATA within the advertised window must not be a FLOW_CONTROL_ERROR");
            Assert.AreEqual(0, stream.RecvWindowSize);

            clientStream.Release();
            Assert.AreEqual(16384, await WithWatchdogResult(read, ct));
            Assert.IsTrue(conn.IsAlive);
        }

        // --- #30: stream window overflow is a stream error ---

        [Test]
        public void FlowControl_StreamWindowUpdateOverflow_ResetsOnlyThatStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/a"));
                var taskA = conn.SendRequestAsync(requestA, new RequestContext(requestA), cts.Token).AsTask();
                var headersA = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/b"));
                var taskB = conn.SendRequestAsync(requestB, new RequestContext(requestB), cts.Token).AsTask();
                var headersB = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);

                // RFC 9113 Section 6.9.1: 65535 + (2^31-1) overflows the stream window -> RST_STREAM.
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(headersA.StreamId, int.MaxValue), cts.Token);

                var rst = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.RstStream, cts.Token);
                Assert.AreEqual(headersA.StreamId, rst.StreamId);
                Assert.AreEqual((byte)Http2ErrorCode.FlowControlError, rst.Payload[3]);

                try
                {
                    await WithWatchdogResult(taskA, cts.Token);
                    Assert.Fail("the reset stream's request must fail");
                }
                catch (UHttpException ex)
                {
                    Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                }

                // The connection and the other stream are unaffected.
                Assert.IsTrue(conn.IsAlive);
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headersB.StreamId, 200, endStream: true), cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, (await WithWatchdogResult(taskB, cts.Token)).StatusCode);
                Assert.IsTrue(conn.IsAlive);

                conn.Dispose();
            });
        }

        // --- helpers ---

        private static Task<UHttpResponse> SendPostAsync(
            Http2Connection conn, string path, int bodyLength, CancellationToken ct)
        {
            var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com" + path))
                .WithBody(Enumerable.Repeat((byte)'u', bodyLength).ToArray());
            return conn.SendRequestAsync(request, new RequestContext(request), ct).AsTask();
        }

        /// <summary>Reads frames until one matches; a GOAWAY on the way fails the test.</summary>
        private static async Task<Http2Frame> ReadFrameMatchingAsync(
            Http2FrameCodec serverCodec, Func<Http2Frame, bool> match, CancellationToken ct)
        {
            while (true)
            {
                var frame = await serverCodec.ReadFrameAsync(16384, ct);
                if (frame.Type == Http2FrameType.GoAway)
                {
                    Assert.Fail($"unexpected GOAWAY, error code {(frame.Length >= 8 ? frame.Payload[7] : -1)}");
                }

                if (match(frame))
                    return frame;
            }
        }

        /// <summary>Reads DATA until every listed stream sent END_STREAM; returns bytes per stream.</summary>
        private static async Task<ConcurrentDictionary<int, int>> ReadUploadsToEndAsync(
            Http2FrameCodec serverCodec, int[] streamIds, CancellationToken ct)
        {
            var received = new ConcurrentDictionary<int, int>();
            int open = streamIds.Length;
            foreach (var id in streamIds)
                received[id] = 0;

            while (open > 0)
            {
                var data = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Data, ct);
                Assert.IsTrue(received.ContainsKey(data.StreamId), $"DATA on unexpected stream {data.StreamId}");
                received[data.StreamId] += data.Length;
                if (data.HasFlag(Http2FrameFlags.EndStream))
                    open--;
            }

            return received;
        }

        private static async Task<T> WithWatchdogResult<T>(Task<T> task, CancellationToken watchdog)
        {
            await WithWatchdog(task, watchdog);
            return await task;
        }

        private static ConcurrentDictionary<int, Http2Stream> GetActiveStreams(Http2Connection conn)
        {
            var field = typeof(Http2Connection).GetField("_activeStreams", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (ConcurrentDictionary<int, Http2Stream>)field.GetValue(conn);
        }

        private static void SetConnectionSendWindow(Http2Connection conn, int value)
        {
            var field = typeof(Http2Connection).GetField(
                "_connectionSendWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(conn, value);
        }

        private static int ReadWindowIncrement(Http2Frame frame)
        {
            Assert.AreEqual(4, frame.Length);
            return ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                   (frame.Payload[2] << 8) | frame.Payload[3];
        }

        private static Http2Frame BuildRstStreamFrame(int streamId, Http2ErrorCode code)
        {
            uint value = (uint)code;
            return new Http2Frame
            {
                Type = Http2FrameType.RstStream,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = new[]
                {
                    (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
                },
                Length = 4
            };
        }

        private static Http2Frame BuildDataFrame(int streamId, int length)
        {
            return new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlags.None,
                StreamId = streamId,
                Payload = new byte[length],
                Length = length
            };
        }

        private static async Task InitializeWithEmptySettingsAsync(
            Http2Connection conn, TestDuplexStream duplex, Http2FrameCodec serverCodec, CancellationToken ct)
        {
            var serverTask = Task.Run(async () =>
            {
                var preface = new byte[24];
                int read = 0;
                while (read < 24)
                {
                    int n = await duplex.ServerStream.ReadAsync(preface, read, 24 - read, ct);
                    if (n == 0) throw new IOException("Unexpected end of stream");
                    read += n;
                }

                await serverCodec.ReadFrameAsync(16384, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, ct);
                await serverCodec.ReadFrameAsync(16384, ct);
            }, ct);

            await conn.InitializeAsync(ct);
            await serverTask;
        }

        /// <summary>Handler that records the dispatch outcome without collecting a body.</summary>
        private sealed class RecordingHandler : IHttpHandler
        {
            public int StatusCode { get; private set; }
            public UHttpException Error { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode, HttpHeaders headers, IResponseBodySource body, RequestContext context)
            {
                StatusCode = statusCode;
                body?.Abort();
                return default;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                Error = error;
            }
        }

        /// <summary>
        /// Client-side stream decorator that passes every write through to the peer but can hold the
        /// return of one stream WINDOW_UPDATE write after its bytes were delivered, so the peer can act
        /// on the frame while the writer is still inside the call. Also signals when a read starts after
        /// a byte count (the frame reader reads exact lengths, so that proves earlier frames were handled).
        /// </summary>
        private sealed class PostWriteHoldStream : Stream
        {
            private const int FrameHeaderSize = 9;
            private readonly Stream _inner;
            private readonly object _gate = new object();
            private int _holdStreamId;
            private TaskCompletionSource<bool> _held;
            private TaskCompletionSource<bool> _release;
            private long _bytesRead;
            private long _readWaitThreshold;
            private TaskCompletionSource<bool> _readWaiter;

            // Write-side frame tracking; writes are serialized by the connection's write lock.
            private readonly byte[] _header = new byte[FrameHeaderSize];
            private int _headerFilled;
            private int _payloadRemaining;
            private bool _prefaceSkipped;

            public PostWriteHoldStream(Stream inner)
            {
                _inner = inner;
            }

            public Task HoldAfterWindowUpdate(int streamId)
            {
                lock (_gate)
                {
                    _holdStreamId = streamId;
                    _held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _held.Task;
                }
            }

            public void Release()
            {
                TaskCompletionSource<bool> release;
                lock (_gate)
                    release = _release;
                release?.TrySetResult(true);
            }

            public Task WaitForReadStartedAfter(long totalBytes)
            {
                lock (_gate)
                {
                    _readWaitThreshold = totalBytes;
                    _readWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _readWaiter.Task;
                }
            }

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                OnReadStarting();
                int read = _inner.Read(buffer, offset, count);
                Interlocked.Add(ref _bytesRead, read);
                return read;
            }

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                OnReadStarting();
                int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _bytesRead, read);
                return read;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                Track(buffer, offset, count);
            }

            public override async Task WriteAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                var release = Track(buffer, offset, count);
                if (release != null)
                    await release.Task.ConfigureAwait(false);
            }

            public override async ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var copy = buffer.ToArray();
                await WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Release();
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            /// <summary>Returns the release task when this write completed a held WINDOW_UPDATE.</summary>
            private TaskCompletionSource<bool> Track(byte[] buffer, int offset, int count)
            {
                if (!_prefaceSkipped)
                {
                    // The 24-byte connection preface is the first write.
                    _prefaceSkipped = true;
                    if (count == Http2Constants.ConnectionPreface.Length)
                        return null;
                }

                TaskCompletionSource<bool> release = null;
                int end = offset + count;
                while (offset < end)
                {
                    if (_headerFilled < FrameHeaderSize)
                    {
                        int take = Math.Min(FrameHeaderSize - _headerFilled, end - offset);
                        Buffer.BlockCopy(buffer, offset, _header, _headerFilled, take);
                        _headerFilled += take;
                        offset += take;
                        if (_headerFilled < FrameHeaderSize)
                            break;
                        _payloadRemaining = (_header[0] << 16) | (_header[1] << 8) | _header[2];
                    }
                    else
                    {
                        int take = Math.Min(_payloadRemaining, end - offset);
                        _payloadRemaining -= take;
                        offset += take;
                    }

                    if (_headerFilled == FrameHeaderSize && _payloadRemaining == 0)
                    {
                        int streamId = ((_header[5] & 0x7F) << 24) | (_header[6] << 16) |
                                       (_header[7] << 8) | _header[8];
                        if (_header[3] == (byte)Http2FrameType.WindowUpdate)
                            release = TryHold(streamId) ?? release;
                        _headerFilled = 0;
                    }
                }

                return release;
            }

            private TaskCompletionSource<bool> TryHold(int streamId)
            {
                lock (_gate)
                {
                    if (_held == null || _holdStreamId != streamId || _held.Task.IsCompleted)
                        return null;
                    _held.TrySetResult(true);
                    return _release;
                }
            }

            private void OnReadStarting()
            {
                TaskCompletionSource<bool> waiter = null;
                lock (_gate)
                {
                    if (_readWaiter != null && Interlocked.Read(ref _bytesRead) >= _readWaitThreshold)
                    {
                        waiter = _readWaiter;
                        _readWaiter = null;
                    }
                }

                waiter?.TrySetResult(true);
            }
        }
    }
}
