using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Tests.Transport.Http2.Helpers;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2
{
    /// <summary>
    /// Request header fields are validated (RFC 9113 Sections 8.2.1 and 8.3) before a stream id is
    /// allocated or any frame is written (#26).
    /// </summary>
    public partial class Http2ConnectionTests
    {
        [TestCase("X-Test", "bad\r\nInjected: yes")]
        [TestCase("X-Test", "bad\nvalue")]
        [TestCase("X-Test", "bad\rvalue")]
        [TestCase("X-Test", "nul\0value")]
        [TestCase("X-Test", "ctl\u0001value")]
        [TestCase("X-Test", "del\u007Fvalue")]
        [TestCase("X-Test", " leading")]
        [TestCase("X-Test", "trailing\t")]
        [TestCase(":path", "/injected")]
        [TestCase(":authority", "evil.example.com")]
        [TestCase("X Bad", "value")]
        [TestCase("X-Bad\r\nInjected", "value")]
        [TestCase("X-Bad\0", "value")]
        public void SendRequest_WithInvalidRequestHeader_FailsBeforeAnyFrame_AndConsumesNoStreamId(
            string name, string value)
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var headers = new HttpHeaders();
                headers.Set(name, value);
                var bad = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/path"), headers);

                UHttpException ex = null;
                try
                {
                    await conn.SendRequestAsync(bad, new RequestContext(bad), cts.Token);
                    Assert.Fail("Expected request header validation to fail.");
                }
                catch (UHttpException caught)
                {
                    ex = caught;
                }

                Assert.IsNotNull(ex);
                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                Assert.IsFalse(ex.HttpError.IsRetryable());
                Assert.IsInstanceOf<ArgumentException>(ex.InnerException);
                if (value.IndexOf('\0') >= 0 || value.IndexOf('\u0001') >= 0)
                    StringAssert.DoesNotContain(value, ex.Message, "the value must not reach the message");

                // Nothing was written for the rejected request: the next frame on the wire is the
                // HEADERS of the next request, and it still uses the first client stream id.
                var good = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/ok"));
                var responseTask = conn.SendRequestAsync(good, new RequestContext(good), cts.Token);

                var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, frame.Type);
                Assert.AreEqual(1, frame.StreamId);

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(frame.StreamId, 200, endStream: true),
                    cts.Token);
                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendRequest_WithUppercaseHeaderNameAndInnerHtab_LowercasesAndSends()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                var headers = new HttpHeaders();
                headers.Set("X-Custom-Header", "a\tb c");
                headers.Set("TE", "trailers");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.example.com/path"), headers);
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token);

                var frame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, frame.Type);
                var decoded = new HpackDecoder().Decode(frame.Payload, 0, frame.Length);
                Assert.IsTrue(decoded.Any(h => h.Name == "x-custom-header" && h.Value == "a\tb c"));
                Assert.IsTrue(decoded.Any(h => h.Name == "te" && h.Value == "trailers"));
                Assert.IsFalse(decoded.Any(h => h.Name.Any(char.IsUpper)));

                await serverCodec.WriteFrameAsync(
                    BuildResponseHeadersFrame(frame.StreamId, 200, endStream: true),
                    cts.Token);
                var response = await responseTask;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                conn.Dispose();
            });
        }

        [Test]
        public void SendPostRequest_WithNulInRequestTrailerValue_FailsAsInvalidRequestAndResetsStream()
        {
            AssertAsync.Run(async () =>
            {
                using var cts = new CancellationTokenSource(10000);
                var (conn, serverStream, _) = await CreateInitializedConnectionAsync(cts.Token);
                var serverCodec = new Http2FrameCodec(serverStream);

                using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream body"), writable: false);
                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://test.example.com/api"))
                    .WithStreamBody(bodyStream, bodyStream.Length, leaveOpen: true)
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc\0");
                            return trailers;
                        });
                var responseTask = conn.SendRequestAsync(request, new RequestContext(request), cts.Token);

                var headersFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.Headers, headersFrame.Type);
                int streamId = headersFrame.StreamId;

                var rstFrame = await serverCodec.ReadFrameAsync(16384, cts.Token);
                Assert.AreEqual(Http2FrameType.RstStream, rstFrame.Type);
                Assert.AreEqual(streamId, rstFrame.StreamId);

                UHttpException ex = null;
                try
                {
                    await responseTask;
                    Assert.Fail("Expected request trailer validation to fail.");
                }
                catch (UHttpException caught)
                {
                    ex = caught;
                }

                Assert.IsNotNull(ex);
                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                StringAssert.Contains("control character 0x00", ex.Message);
                Assert.IsInstanceOf<ArgumentException>(ex.InnerException);

                conn.Dispose();
            });
        }
    }
}
