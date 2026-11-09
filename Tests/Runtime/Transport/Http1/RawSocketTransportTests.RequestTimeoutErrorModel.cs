using System;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Transport.Http1
{
    public partial class RawSocketTransportTests
    {
        // #409: on HTTP/1.1 too, a request deadline surfaces as UHttpErrorType.Timeout from SendStreamingAsync
        // and from a stalled upload, never as an OperationCanceledException the caller did not ask for (the
        // buffered body phase is covered by RawSocketTransport_BufferedBodyRead_StillRespectsRequestTimeout).
        [Test]
        public void RawSocketTransport_StreamingResponseHeadNeverArrives_RequestTimeoutIsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    // Reads the request and never answers; the connection stays open until the server is disposed.
                    await ReadRequestHeadersAsync(client.GetStream());
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    AllowCleartext = true,
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var failure = await CaptureFailureAsync(async () =>
                {
                    await using var response = await client.Get($"http://127.0.0.1:{server.Port}/never")
                        .WithTimeout(TimeSpan.FromMilliseconds(300))
                        .SendStreamingAsync();
                });

                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)failure).HttpError.Type, failure.ToString());
            });
        }

        [Test]
        public void RawSocketTransport_UploadBlockedByPeerNotReading_RequestTimeoutIsTimeout()
        {
            AssertAsync.Run(async () =>
            {
                using var server = new HttpServer(async (client, _) =>
                {
                    // Reads the request head and never the body: the client's write blocks once the socket
                    // buffers are full.
                    await ReadRequestHeadersAsync(client.GetStream());
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    AllowCleartext = true,
                    Transport = new RawSocketTransport(),
                    DisposeTransport = true
                });

                var failure = await CaptureFailureAsync(async () =>
                {
                    using var response = await client.Post($"http://127.0.0.1:{server.Port}/upload")
                        .WithBody(new byte[32 * 1024 * 1024])
                        .WithTimeout(TimeSpan.FromMilliseconds(500))
                        .SendBufferedAsync();
                });

                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                Assert.AreEqual(UHttpErrorType.Timeout, ((UHttpException)failure).HttpError.Type, failure.ToString());
            });
        }

        private static async Task<Exception> CaptureFailureAsync(Func<Task> action)
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
