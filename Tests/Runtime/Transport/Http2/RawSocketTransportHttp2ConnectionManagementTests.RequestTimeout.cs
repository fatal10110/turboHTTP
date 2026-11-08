using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #409: once an HTTP/2 response has started, its body is read outside the transport's failure mapping.
    /// A request deadline that fires then (the body stalls, or the upload is still blocked on the peer's
    /// flow-control window, RFC 9113 Section 6.9) must surface as <see cref="UHttpErrorType.Timeout"/>, and
    /// only the caller's own token may surface as an <see cref="OperationCanceledException"/>.
    /// </summary>
    public partial class RawSocketTransportHttp2ConnectionManagementTests
    {
        [Test]
        public void RequestTimeout_AfterResponseStart_BufferedBody_IsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();

                // The request (headers and body) is read in full, then the response starts and its body
                // never comes: only the request deadline can end the exchange.
                harness.AddIdleConnection(
                    (codec, ct) => ServeAsync(codec, (c, request) => WriteResponseHeadersAsync(c, request.StreamId, ct), ct),
                    cts.Token);

                using var client = NewClient(harness);
                var request = client.Post($"https://{Host}:{Port}/echo")
                    .WithBody(new byte[4096])
                    .WithTimeout(TimeSpan.FromSeconds(1));

                var failure = await CaptureAsync(() => client.SendBufferedAsync(request, cts.Token));

                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)failure).HttpError.Type, failure.ToString());
                Assert.IsFalse(cts.IsCancellationRequested);
            });
        }

        [Test]
        public void RequestTimeout_DuringUploadAfterResponseStart_StreamingBody_IsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var harness = new Harness();
                AddEarlyResponseConnection(harness, cts.Token);

                using var client = NewClient(harness);
                var request = client.Post($"https://{Host}:{Port}/upload")
                    .WithBody(new byte[4096])
                    .WithTimeout(TimeSpan.FromSeconds(1));

                // The response started while the upload is blocked on a zero send window, so the request
                // deadline still governs the exchange when the body read is pending.
                await using var response = await client.SendStreamingAsync(request, cts.Token);
                var failure = await CaptureAsync(() => response.Body.ReadAsync(new byte[16], 0, 16, CancellationToken.None));

                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)failure).HttpError.Type, failure.ToString());
            });
        }

        [Test]
        public void CallerCancel_DuringUploadAfterResponseStart_StreamingBody_IsTheCallersCancellation()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(20000);
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                using var harness = new Harness();
                AddEarlyResponseConnection(harness, cts.Token);

                using var client = NewClient(harness);
                var request = client.Post($"https://{Host}:{Port}/upload").WithBody(new byte[4096]);

                await using var response = await client.SendStreamingAsync(request, caller.Token);
                var read = response.Body.ReadAsync(new byte[16], 0, 16, CancellationToken.None);
                Assert.IsFalse(read.IsCompleted, "no body byte was sent");
                caller.Cancel();

                // The body is read with another token: the send token's cancellation keeps its shape.
                var failure = await CaptureAsync(() => read);
                Assert.IsInstanceOf<OperationCanceledException>(failure, failure?.ToString());
                Assert.AreEqual(caller.Token, ((OperationCanceledException)failure).CancellationToken);
            });
        }

        private static UHttpClient NewClient(Harness harness) =>
            new UHttpClient(new UHttpClientOptions { Transport = harness.Transport, DisposeTransport = false });

        /// <summary>
        /// SETTINGS_INITIAL_WINDOW_SIZE = 0, and a 200 without END_STREAM as soon as a request's HEADERS
        /// arrive: the upload stays blocked on flow control while the response has started.
        /// </summary>
        private static void AddEarlyResponseConnection(Harness harness, CancellationToken token)
        {
            var zeroInitialWindow = new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };
            harness.AddIdleConnection(async (codec, server, ct) =>
            {
                while (true)
                {
                    var frame = await codec.ReadFrameAsync(16384, ct);
                    if (frame.Type == Http2FrameType.Headers)
                        await WriteResponseHeadersAsync(codec, frame.StreamId, ct);
                }
            }, zeroInitialWindow, token);
        }

        private static async Task WriteResponseHeadersAsync(Http2FrameCodec codec, int streamId, CancellationToken ct)
        {
            var headerBlock = new HpackEncoder().Encode(new List<(string, string)> { (":status", "200") }).ToArray();
            await codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Headers,
                Flags = Http2FrameFlags.EndHeaders,
                StreamId = streamId,
                Payload = headerBlock,
                Length = headerBlock.Length
            }, ct);
        }

        private static async Task<Exception> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
