using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// #46: :authority carries the same authority as the HTTP/1.1 Host header (RFC 9113 Section
    /// 8.3.1): the A-label form of an IDN host and an IPv6 literal in exactly one pair of brackets.
    /// </summary>
    public partial class Http2ConnectionTests
    {
        [TestCase("https://münchen.de/", "xn--mnchen-3ya.de")]
        [TestCase("https://例え.テスト:8443/", "xn--r8jz45g.xn--zckzah:8443")]
        [TestCase("https://127.0.0.1/", "127.0.0.1")]
        [TestCase("https://127.0.0.1:8443/", "127.0.0.1:8443")]
        [TestCase("https://[::1]/", "[::1]")]
        [TestCase("https://[::1]:8443/", "[::1]:8443")]
        public void SendRequest_Authority_IsAsciiAndSingleBracketed(string uri, string expected)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var request = new UHttpRequest(HttpMethod.GET, new Uri(uri));
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token).AsTask();

                var headers = await ReadHeaderBlockAsync(serverCodec, cts.Token);
                var decoded = new HpackDecoder().Decode(headers.Block, 0, headers.Block.Length);
                var authority = decoded.Where(h => h.Name == ":authority").Select(h => h.Value).ToList();
                CollectionAssert.AreEqual(new[] { expected }, authority);
                Assert.IsFalse(decoded.Any(h => h.Name == "host"), "h2 sends :authority, not Host");

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(headers.StreamId, 200, endStream: true), cts.Token);
                using var response = await responseTask;
                Assert.AreEqual(200, (int)response.StatusCode);
            });
        }
    }
}
