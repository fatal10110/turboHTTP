using System;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #340: an :authority whose ASCII host is not an RFC 3986 host ("xn-- -dha.de" from a
    /// no-break space) fails with InvalidRequest before a stream id is taken (RFC 9113 Section 8.3.1).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        [Test]
        public void SendRequest_HostNotAnRfc3986Host_FailsWithInvalidRequest_AndTakesNoStreamId()
        {
            var uri = new Uri("https://" + HttpAuthorityRfc3986Tests.NoBreakSpaceHost + "/");

            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var bad = new UHttpRequest(HttpMethod.GET, uri);
                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await conn.SendRequestAsync(bad, new RequestContext(bad), cts.Token));
                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                Assert.IsFalse(ex.HttpError.IsRetryable());
                StringAssert.DoesNotContain("xn--", ex.HttpError.Message);

                // The next request on the connection still gets stream 1: nothing was sent or reserved.
                var good = new UHttpRequest(HttpMethod.GET, new Uri("https://example.com/"));
                var responseTask = conn.SendRequestAsync(good, new RequestContext(good), cts.Token).AsTask();
                var headers = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                Assert.AreEqual(1, headers.StreamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);
                using var response = await responseTask;
                Assert.AreEqual(200, (int)response.StatusCode);
            });
        }
    }
}
