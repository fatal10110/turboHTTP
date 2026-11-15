using System;
using System.Collections.Generic;
using System.IO;
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
    /// Stream lifecycle hardening: no completion signal after the pooled stream was released (#731), an
    /// upload failure after response start fails the body (#716), a failed connection closes its
    /// transport stream (#694, RFC 9113 Section 5.4.1), and response pseudo-header rules (#697, RFC 9113
    /// Section 8.3).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        // --- #731: late handler signal after the pooled stream reference was released ---

        [Test]
        public void HandlerFailsAfterCancelledUploadAndDisposedBody_NextRequestOnPooledStreamIsUnaffected()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // RFC 9113 Section 6.9.2: a zero initial window blocks the upload after HEADERS.
                await WriteSettingsAndReadAckAsync(
                    serverCodec, BuildSettingsPayload(Http2SettingId.InitialWindowSize, 0), cts.Token);

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"))
                    .WithBody(new byte[4096]);
                var handler = new PendingResponseStartHandler();
                var dispatch = conn.DispatchAsync(request, handler, new RequestContext(request), requestCts.Token);

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                var stream = PoolLeakCaptureStreams(conn, 1)[0];

                // RFC 9113 Section 8.1: the response starts while the upload is still blocked.
                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: false), cts.Token);
                var body = await WithWatchdogResult(handler.BodyTask, cts.Token);

                // The request is cancelled: the dispatch leaves without awaiting the completion and
                // releases its reference.
                requestCts.Cancel();
                await AssertCancelledAsync(dispatch, cts.Token);
                await ReadFrameMatchingAsync(serverCodec,
                    f => f.Type == Http2FrameType.RstStream && f.StreamId == headers.StreamId, cts.Token);

                // The body source's reference goes next, while the handler is still pending; then the
                // handler fails (its continuation runs inline here, so its signal has landed when Fail
                // returns).
                await body.DisposeAsync();
                handler.Fail(new InvalidOperationException("late handler failure"));

                PoolLeakAssertReleased(conn, new[] { stream }, "after the late handler failure");
                Assert.AreEqual(0, GetActiveStreams(conn).Count);

                // The next request rents the same pooled object (the pool is LIFO); a signal raised for
                // the previous rental must not complete it.
                var next = PoolLeakGetAsync(conn, "/next", cts.Token);
                var nextHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                var nextStreams = GetActiveStreams(conn);
                Assert.IsTrue(nextStreams.TryGetValue(nextHeaders.StreamId, out var nextStream),
                    "the next request is still in flight (a stale completion signal would already have failed it)");
                Assert.AreSame(stream, nextStream, "the pooled stream object was reused");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(nextHeaders.StreamId, 200, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(nextHeaders.StreamId, "ok", endStream: true), cts.Token);
                using (var response = await WithWatchdogResult(next, cts.Token))
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                Assert.IsTrue(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- #716: request-body failure after response HEADERS ---

        [Test]
        public void RequestBodyFailsAfterResponseHeaders_BodyReaderFaultsAndBufferedBytesAreReleased()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var requestBody = new GatedFailingReadStream();
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/upload"))
                    .WithStreamBody(requestBody, contentLength: 4096);
                var handler = new PendingResponseStartHandler();
                var dispatch = conn.DispatchAsync(request, handler, new RequestContext(request), cts.Token);

                var headers = await ReadFrameMatchingAsync(serverCodec, f => f.Type == Http2FrameType.Headers, cts.Token);
                await WithWatchdog(requestBody.ReadStarted, cts.Token);
                var stream = PoolLeakCaptureStreams(conn, 1)[0];

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: false), cts.Token);
                await serverCodec.WriteFrameAsync(BuildDataFrame(headers.StreamId, "abc", endStream: false), cts.Token);
                var body = await WithWatchdogResult(handler.BodyTask, cts.Token);
                await PoolLeakPingBarrierAsync(serverCodec, cts.Token);
                Assert.AreEqual(3, ReadConnectionBufferedBytes(conn), "the DATA payload is buffered");

                // The request body source fails after the response started.
                requestBody.Fail();
                await TestHelpers.AssertThrowsAsync<UHttpException>(async () => await WithWatchdog(dispatch, cts.Token));
                await AssertResetAsync(serverCodec, headers.StreamId, Http2ErrorCode.Cancel, cts.Token);
                Assert.AreEqual(0, GetActiveStreams(conn).Count, "the reset removed the stream");

                // The reader gets the failure instead of waiting for DATA that never comes.
                var readFailure = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdog(ReadBodyToEndAsync(body, cts.Token), cts.Token));
                Assert.AreEqual(UHttpErrorType.NetworkError, readFailure.HttpError.Type);

                await body.DisposeAsync();
                Assert.AreEqual(0, ReadConnectionBufferedBytes(conn), "the faulted body released its buffered bytes");

                handler.Complete();
                PoolLeakAssertReleased(conn, new[] { stream }, "after the faulted body was disposed");
                Assert.IsTrue(conn.IsAlive, "a request-body failure is stream-scoped");
                conn.Dispose();
            });
        }

        // --- #694: a failed connection closes its transport stream (RFC 9113 Section 5.4.1) ---

        [Test]
        public void PeerClosesIdleConnection_ClientClosesTransportWithoutAnotherRequest()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);

                // The server's FIN with no request in flight: the read loop sees EOF.
                serverStream.Dispose();

                // The client closes its end too, without a request evicting the connection.
                await WithWatchdog(DrainToEndOfStreamAsync(serverStream, cts.Token), cts.Token);
                Assert.IsFalse(conn.IsAlive);
                conn.Dispose();
            });
        }

        [Test]
        public void ConnectionError_SendsGoAwayThenClosesTransport()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                // RFC 9113 Section 6.9: a zero increment on the connection is a connection error.
                await serverCodec.WriteFrameAsync(BuildWindowUpdateFrame(0, 0), cts.Token);

                Http2Frame goAway;
                do
                {
                    goAway = await serverCodec.ReadFrameAsync(16384, cts.Token);
                }
                while (goAway.Type != Http2FrameType.GoAway);

                Assert.AreEqual((byte)Http2ErrorCode.ProtocolError, goAway.Payload[7]);

                // RFC 9113 Section 5.4.1: after the GOAWAY the TCP connection is closed.
                await WithWatchdog(DrainToEndOfStreamAsync(serverStream, cts.Token), cts.Token);
                Assert.IsFalse(conn.IsAlive);
                conn.Dispose();
            });
        }

        // --- #697: pseudo-header ordering and :status format (RFC 9113 Sections 8.3, 8.3.2) ---

        [TestCase("pseudo-after-regular", "Pseudo-header :status after a regular header field")]
        [TestCase("repeated-status", "Repeated :status pseudo-header")]
        [TestCase("status-0200", "Invalid :status value")]
        [TestCase("status-20", "Invalid :status value")]
        [TestCase("status-plus", "Invalid :status value")]
        public void MalformedStatusPseudoHeader_IsStreamErrorProtocolError(string variant, string expectedReason)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/" + variant));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();
                var requestHeaders = await PoolLeakReadHeadersAsync(serverCodec, cts.Token);
                int streamId = requestHeaders.StreamId;

                List<(string, string)> fields;
                switch (variant)
                {
                    case "pseudo-after-regular":
                        fields = new List<(string, string)> { ("content-type", "text/plain"), (":status", "200") };
                        break;
                    case "repeated-status":
                        fields = new List<(string, string)> { (":status", "200"), (":status", "204") };
                        break;
                    case "status-0200":
                        fields = new List<(string, string)> { (":status", "0200") };
                        break;
                    case "status-20":
                        fields = new List<(string, string)> { (":status", "20") };
                        break;
                    default:
                        fields = new List<(string, string)> { (":status", "+20") };
                        break;
                }

                byte[] headerBlock = new HpackEncoder().Encode(fields).ToArray();
                await serverCodec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Headers,
                    Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    StreamId = streamId,
                    Payload = headerBlock,
                    Length = headerBlock.Length
                }, cts.Token);

                var ex = await TestHelpers.AssertThrowsAsync<UHttpException>(
                    async () => await WithWatchdog(responseTask, cts.Token));
                Assert.That(ex.Message, Does.Contain(expectedReason));

                // RFC 9113 Section 8.1.1: a malformed response is a stream error of type PROTOCOL_ERROR.
                await AssertResetAsync(serverCodec, streamId, Http2ErrorCode.ProtocolError, cts.Token);
                Assert.IsTrue(conn.IsAlive, "a malformed response fails only its stream");
                conn.Dispose();
            });
        }

        [TestCase("200", true, 200)]
        [TestCase("100", true, 100)]
        [TestCase("999", true, 999)]
        [TestCase("099", false, 0)]
        [TestCase("0200", false, 0)]
        [TestCase("20", false, 0)]
        [TestCase(" 20", false, 0)]
        [TestCase("+20", false, 0)]
        [TestCase("2a0", false, 0)]
        [TestCase("", false, 0)]
        public void TryParseStatusCode_AcceptsExactlyThreeDigits(string value, bool expected, int expectedCode)
        {
            Assert.AreEqual(expected, Http2Connection.TryParseStatusCode(value, out int code));
            Assert.AreEqual(expectedCode, code);
        }

        private static long ReadConnectionBufferedBytes(Http2Connection conn)
        {
            var field = typeof(Http2Connection).GetField(
                "_connectionBufferedBytes", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (long)field.GetValue(conn);
        }

        private static async Task ReadBodyToEndAsync(IResponseBodySource body, CancellationToken ct)
        {
            var buffer = new byte[64];
            while (await body.ReadAsync(buffer, ct) > 0)
            {
            }
        }

        private static async Task DrainToEndOfStreamAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer, 0, buffer.Length, ct) > 0)
            {
            }
        }

        /// <summary>
        /// Captures the response body and keeps OnResponseStartAsync pending until the test completes or
        /// fails it. The returned task runs its continuations inline, so the transport's handling of the
        /// outcome has finished when <see cref="Fail"/> or <see cref="Complete"/> returns.
        /// </summary>
        private sealed class PendingResponseStartHandler : IHttpHandler
        {
            private readonly TaskCompletionSource<IResponseBodySource> _body =
                new TaskCompletionSource<IResponseBodySource>(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _outcome = new TaskCompletionSource<bool>();

            public Task<IResponseBodySource> BodyTask => _body.Task;

            public void Fail(Exception error) => _outcome.SetException(error);

            public void Complete() => _outcome.SetResult(true);

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                _body.TrySetResult(body);
                return new ValueTask(_outcome.Task);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }

        /// <summary>A request body whose first read blocks until <see cref="Fail"/>, then throws.</summary>
        private sealed class GatedFailingReadStream : Stream
        {
            private readonly TaskCompletionSource<bool> _readStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _gate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task ReadStarted => _readStarted.Task;

            public void Fail() => _gate.TrySetResult(true);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("the transport reads request bodies asynchronously");

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                _readStarted.TrySetResult(true);
                await _gate.Task.ConfigureAwait(false);
                throw new IOException("request body source failed");
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
                new ValueTask<int>(ReadAsync(Array.Empty<byte>(), 0, 0, ct));

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
