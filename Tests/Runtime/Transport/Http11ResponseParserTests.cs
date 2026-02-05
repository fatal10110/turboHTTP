using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class Http11ResponseParserTests
    {
        private static async Task<ParsedResponse> ParseAsync(string response, HttpMethod method = HttpMethod.GET)
        {
            var bytes = Encoding.ASCII.GetBytes(response);
            using var ms = new MemoryStream(bytes);
            return await Http11ResponseParser.ParseAsync(ms, method, CancellationToken.None);
        }

        private static async Task<ParsedResponse> ParseAsync(byte[] responseBytes, HttpMethod method = HttpMethod.GET)
        {
            using var ms = new MemoryStream(responseBytes);
            return await Http11ResponseParser.ParseAsync(ms, method, CancellationToken.None);
        }

        [Test]
        public async Task Parse_200OK_ContentLength_ReturnsBodyAndHeaders()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nX-Test: A\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
            Assert.AreEqual("A", parsed.Headers.Get("X-Test"));
            Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public async Task Parse_404NotFound_ReturnsStatusCode()
        {
            var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.NotFound, parsed.StatusCode);
        }

        [Test]
        public async Task Parse_500InternalServerError_ReturnsStatusCode()
        {
            var response = "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.InternalServerError, parsed.StatusCode);
        }

        [Test]
        public async Task Parse_ChunkedBody_ReassemblesCorrectly()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "5\r\nHello\r\n" +
                           "5\r\nWorld\r\n" +
                           "0\r\n\r\n";

            var parsed = await ParseAsync(response);
            Assert.AreEqual("HelloWorld", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public async Task Parse_ChunkedBody_WithTrailers_Completes()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "1\r\nA\r\n" +
                           "0\r\n" +
                           "X-Trailer: yes\r\n\r\n";

            var parsed = await ParseAsync(response);
            Assert.AreEqual("A", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public void Parse_ChunkedBody_InvalidHex_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "ZZ\r\nX\r\n0\r\n\r\n";

            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public async Task Parse_ChunkedBody_EmptyChunks_Handled()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "0\r\n\r\n";

            var parsed = await ParseAsync(response);
            Assert.AreEqual(0, parsed.Body.Length);
        }

        [Test]
        public async Task Parse_NoContentLength_NoChunked_ReadsToEnd()
        {
            var response = "HTTP/1.1 200 OK\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public async Task Parse_HeadResponse_WithContentLength_SkipsBody()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nHello";
            var parsed = await ParseAsync(response, HttpMethod.HEAD);
            Assert.AreEqual(0, parsed.Body.Length);
        }

        [Test]
        public async Task Parse_204NoContent_SkipsBody()
        {
            var response = "HTTP/1.1 204 No Content\r\nContent-Length: 5\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(0, parsed.Body.Length);
        }

        [Test]
        public async Task Parse_304NotModified_SkipsBody()
        {
            var response = "HTTP/1.1 304 Not Modified\r\nContent-Length: 5\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(0, parsed.Body.Length);
        }

        [Test]
        public async Task Parse_100Continue_SkippedBeforeFinalResponse()
        {
            var response = "HTTP/1.1 100 Continue\r\n\r\n" +
                           "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
        }

        [Test]
        public async Task Parse_KeepAlive_HTTP11_Default_ReturnsTrue()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.IsTrue(parsed.KeepAlive);
        }

        [Test]
        public async Task Parse_ConnectionClose_ReturnsFalse()
        {
            var response = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.IsFalse(parsed.KeepAlive);
        }

        [Test]
        public async Task Parse_HTTP10_Default_ReturnsFalse()
        {
            var response = "HTTP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.IsFalse(parsed.KeepAlive);
        }

        [Test]
        public async Task Parse_MultipleSetCookieHeaders_AllPreserved()
        {
            var response = "HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            var values = parsed.Headers.GetValues("Set-Cookie");
            Assert.AreEqual(2, values.Count);
        }

        [Test]
        public void Parse_ReadLineAsync_ExceedsMaxLength_ThrowsFormatException()
        {
            var line = new string('a', 8200) + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(line);
            using var ms = new MemoryStream(bytes);
            Assert.ThrowsAsync<FormatException>(async () =>
                await Http11ResponseParser.ReadLineAsync(ms, CancellationToken.None));
        }

        [Test]
        public void Parse_TotalHeaderSize_ExceedsLimit_ThrowsFormatException()
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            var headerValue = new string('a', 8000);
            for (int i = 0; i < 13; i++)
            {
                sb.Append("X-H");
                sb.Append(i);
                sb.Append(": ");
                sb.Append(headerValue);
                sb.Append("\r\n");
            }
            sb.Append("\r\n");

            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(sb.ToString()));
        }

        [Test]
        public async Task Parse_TransferEncodingGzipChunked_ReturnsRawCompressedChunks()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n" +
                           "3\r\nabc\r\n0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual("abc", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public void Parse_TransferEncodingOnlyGzip_ThrowsNotSupportedException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\n\r\n";
            Assert.ThrowsAsync<NotSupportedException>(async () => await ParseAsync(response));
        }

        [Test]
        public async Task Parse_TransferEncoding_TakesPrecedenceOverContentLength()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 100\r\n\r\n" +
                           "1\r\na\r\n0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(1, parsed.Body.Length);
            Assert.AreEqual("a", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public void Parse_MultipleContentLength_Conflicting_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\nHey";
            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public async Task Parse_MultipleContentLength_Same_Accepted()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 3\r\n\r\nHey";
            var parsed = await ParseAsync(response);
            Assert.AreEqual("Hey", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public async Task Parse_EmptyBody_ReturnsEmptyArray()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(0, parsed.Body.Length);
        }

        [Test]
        public async Task Parse_TransferEncodingIdentity_TreatedAsAbsent()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: identity\r\nContent-Length: 5\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public async Task Parse_StatusLine_NoReasonPhrase_Parses()
        {
            var response = "HTTP/1.1 200\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
        }

        [Test]
        public async Task Parse_StatusLine_EmptyReasonPhrase_Parses()
        {
            var response = "HTTP/1.1 200 \r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
        }

        [Test]
        public async Task Parse_StatusLine_MultiWordReasonPhrase_Parses()
        {
            var response = "HTTP/1.1 200 All Good Here\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
        }

        [Test]
        public void Parse_ContentLength_ParsedAsLong()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ChunkedBody_LargeChunkSizeHex_ParsedAsLong()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "B2D05E00\r\n" +
                           "0\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_1xxResponses_ExceedsMaxIterations_ThrowsFormatException()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 11; i++)
                sb.Append("HTTP/1.1 100 Continue\r\n\r\n");
            sb.Append("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(sb.ToString()));
        }

        [Test]
        public async Task Parse_ChunkedBody_WithExtensions_StripsExtensionsBeforeParsing()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "4;ext=value\r\n" +
                           "Test\r\n" +
                           "0\r\n\r\n";
            var parsed = await ParseAsync(response);
            Assert.AreEqual("Test", Encoding.ASCII.GetString(parsed.Body));
        }

        [Test]
        public void Parse_ChunkedBody_ExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "06400001\r\n" +
                           "0\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_ExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 104857601\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        [Explicit("Allocates >100MB to trigger MaxResponseBodySize")]
        public void Parse_ReadToEnd_ExceedsMaxBodySize_ThrowsIOException()
        {
            var header = "HTTP/1.1 200 OK\r\n\r\n";
            var body = new string('a', 104857601);
            var response = header + body;
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public async Task Parse_MultipleSetCookieHeaders_AllPreservedViaAdd()
        {
            var response = "HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\n\r\n";
            var parsed = await ParseAsync(response);
            var values = parsed.Headers.GetValues("Set-Cookie");
            Assert.AreEqual(2, values.Count);
        }

        [Test]
        public async Task Parse_ReadToEnd_ForcesKeepAliveFalse()
        {
            var response = "HTTP/1.1 200 OK\r\n\r\nHello";
            var parsed = await ParseAsync(response);
            Assert.IsFalse(parsed.KeepAlive);
        }

        [Test]
        public void Parse_ChunkedBody_SingleChunkExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "06400001\r\n" +
                           "0\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_NarrowedToInt_AfterMaxBodySizeCheck()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n";
            Assert.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_Negative_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n";
            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_NonNumeric_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: abc\r\n\r\n";
            Assert.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }
    }
}
