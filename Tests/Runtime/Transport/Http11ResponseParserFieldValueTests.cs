using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Internal;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Received HTTP/1.1 field values (#652, #655): NUL, bare CR, the other C0 controls and DEL in a
    /// header or trailer value reject the message (RFC 9110 Section 5.5, RFC 9112 Section 2.2), and
    /// only SP/HTAB is trimmed as optional whitespace (RFC 9110 Section 5.6.3), so obs-text such as
    /// 0x85/0xA0 stays part of the value and VT/FF never pass as whitespace.
    /// </summary>
    [TestFixture]
    public class Http11ResponseParserFieldValueTests
    {
        // Bytes go through Latin-1 so every char below U+0100 reaches the parser as one octet.
        private static async Task<ParsedResponse> ParseAsync(string response)
        {
            var bytes = EncodingHelper.Latin1.GetBytes(response);
            using var ms = new MemoryStream(bytes);
            var parsed = await Http11ResponseParser.ParseAsync(ms, HttpMethod.GET, CancellationToken.None);
            if (parsed.SegmentedBody != null || parsed.BodyFromPool)
            {
                var copy = parsed.SegmentedBody != null ? parsed.SegmentedBody.ToArray() : parsed.Body.ToArray();
                parsed.ReleaseBodyBuffers();
                parsed.Body = copy;
            }

            return parsed;
        }

        private static string HeaderResponse(string headerValue) =>
            "HTTP/1.1 200 OK\r\nX-A:" + headerValue + "\r\nContent-Length: 0\r\n\r\n";

        private static string TrailerResponse(string trailerValue) =>
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r\n0\r\nX-T:" + trailerValue + "\r\n\r\n";

        // ---- #652: control characters in a field value ----

        [TestCase(" a\0b", TestName = "HeaderValue_Nul_Rejected")]
        [TestCase(" a\rb", TestName = "HeaderValue_BareCr_Rejected")]
        [TestCase(" ab\r", TestName = "HeaderValue_BareCrBeforeLineEnd_Rejected")]
        [TestCase(" a\u0001b", TestName = "HeaderValue_Soh_Rejected")]
        [TestCase(" a\u001Fb", TestName = "HeaderValue_Us_Rejected")]
        [TestCase(" a\u007Fb", TestName = "HeaderValue_Del_Rejected")]
        [TestCase(" a\vb", TestName = "HeaderValue_Vt_Rejected")]
        public void HeaderValue_WithControlCharacter_ThrowsFormatException(string value)
        {
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(HeaderResponse(value)));
        }

        [TestCase(" a\0b", TestName = "TrailerValue_Nul_Rejected")]
        [TestCase(" a\rb", TestName = "TrailerValue_BareCr_Rejected")]
        [TestCase(" ab\r", TestName = "TrailerValue_BareCrBeforeLineEnd_Rejected")]
        [TestCase(" a\u0001b", TestName = "TrailerValue_Soh_Rejected")]
        [TestCase(" a\u001Fb", TestName = "TrailerValue_Us_Rejected")]
        [TestCase(" a\u007Fb", TestName = "TrailerValue_Del_Rejected")]
        [TestCase(" a\fb", TestName = "TrailerValue_Ff_Rejected")]
        public void TrailerValue_WithControlCharacter_ThrowsFormatException(string value)
        {
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(TrailerResponse(value)));
        }

        [Test]
        public void HeaderAndTrailerValue_InteriorHtab_IsAccepted()
        {
            AssertAsync.Run(async () =>
            {
                var header = await ParseAsync(HeaderResponse(" a\tb"));
                Assert.AreEqual("a\tb", header.Headers.Get("X-A"));

                var trailer = await ParseAsync(TrailerResponse(" a\tb"));
                Assert.AreEqual("a\tb", trailer.Trailers.Get("X-T"));
            });
        }

        [Test]
        public void StreamingTrailerValue_WithNul_ThrowsNetworkError()
        {
            AssertAsync.Run(async () =>
            {
                var bodyBytes = EncodingHelper.Latin1.GetBytes("1\r\nA\r\n0\r\nX-T: a\0b\r\n\r\n");
                await using var source = CreateChunkedBodySource(bodyBytes);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(
                    async () => await source.GetTrailersAsync(CancellationToken.None));
                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
            });
        }

        // ---- #655: optional whitespace is SP/HTAB only ----

        [Test]
        public void HeaderAndTrailerValue_SpAndHtabAroundValue_AreTrimmed()
        {
            AssertAsync.Run(async () =>
            {
                var header = await ParseAsync(HeaderResponse(" \t spaced value \t "));
                Assert.AreEqual("spaced value", header.Headers.Get("X-A"));

                var trailer = await ParseAsync(TrailerResponse("\t spaced value \t"));
                Assert.AreEqual("spaced value", trailer.Trailers.Get("X-T"));
            });
        }

        [TestCase(" ", TestName = "HeaderAndTrailerValue_NbspObsText_IsPreserved")]
        [TestCase("\u0085", TestName = "HeaderAndTrailerValue_NelObsText_IsPreserved")]
        public void HeaderAndTrailerValue_ObsTextAtEdges_IsPreserved(string obsText)
        {
            AssertAsync.Run(async () =>
            {
                var value = obsText + "v" + obsText;

                var header = await ParseAsync(HeaderResponse(" " + value + " "));
                Assert.AreEqual(value, header.Headers.Get("X-A"));

                var trailer = await ParseAsync(TrailerResponse(" " + value + " "));
                Assert.AreEqual(value, trailer.Trailers.Get("X-T"));
            });
        }

        [TestCase("Content-Length: \v0", TestName = "ContentLength_LeadingVt_Rejected")]
        [TestCase("Content-Length: 0\f", TestName = "ContentLength_TrailingFf_Rejected")]
        [TestCase("Content-Length: 0 ", TestName = "ContentLength_TrailingNbsp_Rejected")]
        [TestCase("Transfer-Encoding: \fchunked", TestName = "TransferEncoding_LeadingFf_Rejected")]
        [TestCase("Transfer-Encoding: chunked\v", TestName = "TransferEncoding_TrailingVt_Rejected")]
        public void FramingHeader_WithNonOwsWhitespace_ThrowsFormatException(string fieldLine)
        {
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(
                "HTTP/1.1 200 OK\r\n" + fieldLine + "\r\n\r\n0\r\n\r\n"));
        }

        [Test]
        public void ContentLength_SpAndHtabAroundValue_IsAccepted()
        {
            AssertAsync.Run(async () =>
            {
                var parsed = await ParseAsync("HTTP/1.1 200 OK\r\nContent-Length: \t1 \t\r\n\r\na");
                Assert.AreEqual(1, parsed.Body.Length);
            });
        }

        [Test]
        public void StatusLine_StatusCodeFollowedByVt_ThrowsFormatException()
        {
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(
                "HTTP/1.1 200\v\r\nContent-Length: 0\r\n\r\n"));
        }

        [TestCase("1 ", TestName = "ChunkSizeLine_TrailingNbsp_Rejected")]
        [TestCase("1\v", TestName = "ChunkSizeLine_TrailingVt_Rejected")]
        public void ChunkSizeLine_WithNonOwsWhitespace_ThrowsFormatException(string line)
        {
            Assert.Throws<FormatException>(() => Http11ResponseParser.ParseChunkSizeLine(line));
        }

        [Test]
        public void ChunkSizeLine_HtabBeforeExtension_IsAccepted()
        {
            Assert.AreEqual(0x1A, Http11ResponseParser.ParseChunkSizeLine("1A\t;ext=1"));
        }

        [Test]
        public void ExpectContinue_TokenWithObsTextEdge_IsNotMatched()
        {
            var headers = new HttpHeaders();
            headers.Set("Expect", "100-continue ");
            Assert.IsFalse(ExpectContinueHelper.HasOnlyExpectContinueExpectation(headers));
            Assert.IsFalse(ExpectContinueHelper.HeaderValueContainsToken("close\u0085", "close"));

            headers.Set("Expect", " \t100-continue\t ");
            Assert.IsTrue(ExpectContinueHelper.HasOnlyExpectContinueExpectation(headers));
            Assert.IsTrue(ExpectContinueHelper.HeaderValueContainsToken("keep-alive,\tclose ", "close"));
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
