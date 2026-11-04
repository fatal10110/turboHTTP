using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// RFC 9112 strictness of the HTTP/1.1 response parser: EOF inside the head or trailers
    /// (§8), Transfer-Encoding framing rules (§6.1, §6.3), field-line syntax (§5.1, §5.2) and
    /// the status-line HTTP-version (§2.3).
    /// </summary>
    [TestFixture]
    public class Http11ResponseParserStrictnessTests
    {
        private static async Task<ParsedResponse> ParseAsync(string response, HttpMethod method = HttpMethod.GET)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            using var ms = new MemoryStream(bytes);
            var parsed = await Http11ResponseParser.ParseAsync(ms, method, CancellationToken.None);
            if (parsed.SegmentedBody != null || parsed.BodyFromPool)
            {
                var copy = parsed.SegmentedBody != null ? parsed.SegmentedBody.ToArray() : parsed.Body.ToArray();
                parsed.ReleaseBodyBuffers();
                parsed.Body = copy;
            }

            return parsed;
        }

        private static async Task<ParsedResponseHead> ParseHeadAsync(string response, RequestContext context = null)
        {
            var ms = new MemoryStream(Encoding.ASCII.GetBytes(response));
            return await Http11ResponseParser.ParseHeadAsync(ms, HttpMethod.GET, CancellationToken.None, context);
        }

        // ---- #39: EOF inside the response head or trailers ----

        [Test]
        public void Parse_EofMidHeaderLine_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync("HTTP/1.1 200 OK\r\nContent-Le"));
        }

        [Test]
        public void Parse_EofBeforeBlankLine_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"));
        }

        [Test]
        public void Parse_EofMidStatusLine_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync("HTTP/1.1 200 O"));
        }

        [Test]
        public void Parse_EofBeforeAnyStatusLineByte_KeepsEmptyStatusLineSignal()
        {
            var ex = AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(string.Empty));
            Assert.AreEqual("Empty HTTP status line", ex.Message);
        }

        [Test]
        public void Parse_ChunkedBody_EofInsideTrailerSection_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n0\r\nX-T: y\r\n"));
        }

        [Test]
        public void Parse_ChunkedBody_EofMidTrailerLine_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n0\r\nX-T: y"));
        }

        [Test]
        public void Parse_ChunkedBody_EofRightAfterLastChunk_ThrowsIOException()
        {
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n0\r\n"));
        }

        [Test]
        public void Parse_ChunkedBody_CompleteTrailerSection_StillParses()
        {
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync(
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n0\r\nX-T: y\r\n\r\n");
                Assert.AreEqual("a", Encoding.ASCII.GetString(parsed.Body.Span));
                Assert.AreEqual("y", parsed.Trailers.Get("X-T"));
            });
        }

        [Test]
        public void ParseHead_StreamingTrailers_EofInsideTrailerSection_ThrowsNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trailer: ok\r\n");
                await using var source = CreateChunkedBodySource(bodyBytes);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        [Test]
        public void ParseHead_StreamingTrailers_EofMidTrailerLine_ThrowsNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = Encoding.ASCII.GetBytes("1\r\nA\r\n0\r\nX-Trai");
                await using var source = CreateChunkedBodySource(bodyBytes);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        // ---- #40: Transfer-Encoding framing ----

        [Test]
        public void Parse_TransferEncodingSplitAcrossFieldLines_FinalGzip_IsNotChunked()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: gzip\r\n\r\n" +
                           "1\r\na\r\n0\r\n\r\n";
            AssertAsync.ThrowsAsync<NotSupportedException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_TransferEncodingSplitAcrossFieldLines_ChunkedOnly_IsChunked()
        {
            AssertAsync.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: identity\r\nTransfer-Encoding: chunked\r\n\r\n" +
                               "1\r\na\r\n0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("a", Encoding.ASCII.GetString(parsed.Body.Span));
                Assert.IsTrue(parsed.KeepAlive);
            });
        }

        [Test]
        public void Parse_TransferEncodingChunkedTwice_ThrowsNotSupportedException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked, chunked\r\n\r\n1\r\na\r\n0\r\n\r\n";
            AssertAsync.ThrowsAsync<NotSupportedException>(async () => await ParseAsync(response));
        }

        [Test]
        public void ParseHead_TransferEncodingAndContentLength_ClosesConnectionAndDropsContentLength()
        {
            AssertAsync.Run(async () =>
            {
                using var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/"));
                var context = new RequestContext(request);
                using var head = await ParseHeadAsync(
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 100\r\n\r\n1\r\na\r\n0\r\n\r\n",
                    context);

                Assert.AreEqual(Http11ResponseBodyKind.Chunked, head.BodyKind);
                Assert.IsFalse(head.KeepAlive);
                Assert.IsNull(head.ContentLength);
                Assert.IsNull(head.Headers.Get("Content-Length"));
                Assert.That(context.Timeline.Select(evt => evt.Name), Does.Contain("Http11DualFramingHeaders"));
            });
        }

        [Test]
        public void Parse_TransferEncodingAndContentLength_BufferedResponseIsNotReused()
        {
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync(
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 1\r\n\r\n3\r\nabc\r\n0\r\n\r\n");
                Assert.AreEqual("abc", Encoding.ASCII.GetString(parsed.Body.Span));
                Assert.IsFalse(parsed.KeepAlive);
            });
        }

        [Test]
        public void ParseHead_Http10WithTransferEncoding_ReadsToCloseAndIsNotReused()
        {
            AssertAsync.Run(async () =>
            {
                using var head = await ParseHeadAsync(
                    "HTTP/1.0 200 OK\r\nConnection: keep-alive\r\nTransfer-Encoding: chunked\r\nContent-Length: 3\r\n\r\n" +
                    "1\r\na\r\n0\r\n\r\n");

                Assert.AreEqual(Http11ResponseBodyKind.ReadToEnd, head.BodyKind);
                Assert.IsFalse(head.KeepAlive);
                Assert.IsNull(head.ContentLength);
                Assert.IsNull(head.Headers.Get("Content-Length"));
            });
        }

        [Test]
        public void ParseHead_Http10WithTransferEncoding_HeadRequest_IsNotReused()
        {
            AssertAsync.Run(async () =>
            {
                var ms = new MemoryStream(Encoding.ASCII.GetBytes(
                    "HTTP/1.0 200 OK\r\nConnection: keep-alive\r\nTransfer-Encoding: chunked\r\n\r\n"));
                using var head = await Http11ResponseParser.ParseHeadAsync(ms, HttpMethod.HEAD, CancellationToken.None);

                Assert.AreEqual(Http11ResponseBodyKind.Empty, head.BodyKind);
                Assert.IsFalse(head.KeepAlive);
            });
        }

        // ---- #41: field-line syntax ----

        [Test]
        public void Parse_WhitespaceBetweenFieldNameAndColon_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nContent-Length : 0\r\n\r\n"));
        }

        [Test]
        public void Parse_TabBetweenFieldNameAndColon_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nX-A\t: 1\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_ObsFoldContinuationLine_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nX-A: one\r\n two\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_ObsFoldWithTab_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nX-A: one\r\n\tContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_WhitespaceLineBeforeFirstField_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\n X-A: 1\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_NonTokenFieldName_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nX(Bad): 1\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_EmptyFieldName_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\n: value\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_FieldLineWithoutColon_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync("HTTP/1.1 200 OK\r\nNoColonHere\r\nContent-Length: 0\r\n\r\n"));
        }

        [Test]
        public void Parse_ValidFieldLines_TrimOptionalWhitespaceAroundValue()
        {
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync("HTTP/1.1 200 OK\r\nX-Token_1!: \t spaced value \t\r\nContent-Length:0\r\n\r\n");
                Assert.AreEqual("spaced value", parsed.Headers.Get("X-Token_1!"));
                Assert.AreEqual(0, parsed.Body.Length);
            });
        }

        // ---- #174: status-line HTTP-version ----

        [TestCase("HTTP/2.0 200 OK")]
        [TestCase("HTTP/1.10 200 OK")]
        [TestCase("http/1.1 200 OK")]
        [TestCase("HTTP/1. 200 OK")]
        [TestCase("HTTP/9 200 OK")]
        [TestCase("HTTP/1.a 200 OK")]
        [TestCase("HTTP/1.1x 200 OK")]
        public void Parse_InvalidStatusLineVersion_ThrowsFormatException(string statusLine)
        {
            AssertAsync.ThrowsAsync<FormatException>(
                async () => await ParseAsync(statusLine + "\r\nContent-Length: 0\r\n\r\n"));
        }

        [TestCase("HTTP/1.1", true)]
        [TestCase("HTTP/1.0", false)]
        [TestCase("HTTP/1.2", true)]
        [TestCase("HTTP/1.9", true)]
        public void Parse_SupportedStatusLineVersion_Parses(string version, bool expectedKeepAlive)
        {
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync(version + " 200 OK\r\nContent-Length: 0\r\n\r\n");
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual(expectedKeepAlive, parsed.KeepAlive);
            });
        }

        [Test]
        public void Parse_HigherMinorVersionWithTransferEncoding_TreatedAsHttp11()
        {
            // RFC 9110 Section 2.5: HTTP/1.2 is processed as HTTP/1.1, so the HTTP/1.0
            // Transfer-Encoding rule (RFC 9112 Section 6.1) does not apply: chunked framing is used.
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync("HTTP/1.2 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n");
                Assert.AreEqual("abc", Encoding.ASCII.GetString(parsed.Body.Span));
                Assert.IsTrue(parsed.KeepAlive);
            });
        }

        private static Http11ResponseBodySource CreateChunkedBodySource(byte[] bodyBytes)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var stream = new MemoryStream(bodyBytes, writable: false);
            var head = new ParsedResponseHead(new Http11ResponseParser.BufferedStreamReader(stream))
            {
                StatusCode = HttpStatusCode.OK,
                Headers = HttpHeaders.Empty,
                KeepAlive = false,
                BodyKind = Http11ResponseBodyKind.Chunked,
                ContentLength = null
            };

            var lease = new ConnectionLease(
                null,
                new SemaphoreSlim(0, 1),
                new PooledConnection(socket, stream, "example.test", 80, false));

            try
            {
                return new Http11ResponseBodySource(
                    head,
                    lease,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(30));
            }
            finally
            {
                head.Dispose();
            }
        }
    }
}
