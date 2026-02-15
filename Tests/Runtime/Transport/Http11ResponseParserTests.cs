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

        private static async Task<ParsedResponse> ParseFragmentedAsync(
            byte[] responseBytes,
            int[] chunkSizes,
            HttpMethod method = HttpMethod.GET)
        {
            using var stream = new FragmentedReadStream(responseBytes, chunkSizes);
            return await Http11ResponseParser.ParseAsync(stream, method, CancellationToken.None);
        }

        private sealed class FragmentedReadStream : Stream
        {
            private readonly byte[] _buffer;
            private readonly int[] _chunkSizes;
            private int _position;
            private int _chunkIndex;

            public FragmentedReadStream(byte[] buffer, int[] chunkSizes)
            {
                _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
                _chunkSizes = (chunkSizes == null || chunkSizes.Length == 0)
                    ? new[] { 1 }
                    : chunkSizes;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _buffer.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _buffer.Length)
                    return 0;

                var configured = _chunkSizes[_chunkIndex % _chunkSizes.Length];
                _chunkIndex++;

                var toCopy = Math.Min(Math.Min(configured, count), _buffer.Length - _position);
                Buffer.BlockCopy(_buffer, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
        }

        [Test]
        public void Parse_200OK_ContentLength_ReturnsBodyAndHeaders()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nX-Test: A\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual("A", parsed.Headers.Get("X-Test"));
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_404NotFound_ReturnsStatusCode()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.NotFound, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_500InternalServerError_ReturnsStatusCode()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.InternalServerError, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ChunkedBody_ReassemblesCorrectly()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                               "5\r\nHello\r\n" +
                               "5\r\nWorld\r\n" +
                               "0\r\n\r\n";

                var parsed = await ParseAsync(response);
                Assert.AreEqual("HelloWorld", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ChunkedBody_WithTrailers_Completes()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                               "1\r\nA\r\n" +
                               "0\r\n" +
                               "X-Trailer: yes\r\n\r\n";

                var parsed = await ParseAsync(response);
                Assert.AreEqual("A", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ChunkedBody_InvalidHex_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "ZZ\r\nX\r\n0\r\n\r\n";

            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ChunkedBody_EmptyChunks_Handled()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                               "0\r\n\r\n";

                var parsed = await ParseAsync(response);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_NoContentLength_NoChunked_ReadsToEnd()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_HeadResponse_WithContentLength_SkipsBody()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nHello";
                var parsed = await ParseAsync(response, HttpMethod.HEAD);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_204NoContent_SkipsBody()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 204 No Content\r\nContent-Length: 5\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_304NotModified_SkipsBody()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 304 Not Modified\r\nContent-Length: 5\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_100Continue_SkippedBeforeFinalResponse()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 100 Continue\r\n\r\n" +
                               "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_KeepAlive_HTTP11_Default_ReturnsTrue()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.IsTrue(parsed.KeepAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ConnectionClose_ReturnsFalse()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.IsFalse(parsed.KeepAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_HTTP10_Default_ReturnsFalse()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.IsFalse(parsed.KeepAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_MultipleSetCookieHeaders_AllPreserved()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                var values = parsed.Headers.GetValues("Set-Cookie");
                Assert.AreEqual(2, values.Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ReadLineAsync_ExceedsMaxLength_ThrowsFormatException()
        {
            var line = new string('a', 8200) + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(line);
            using var ms = new MemoryStream(bytes);
            AssertAsync.ThrowsAsync<FormatException>(async () =>
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

            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(sb.ToString()));
        }

        [Test]
        public void Parse_TransferEncodingGzipChunked_ReturnsRawCompressedChunks()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n" +
                               "3\r\nabc\r\n0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("abc", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_TransferEncodingOnlyGzip_ThrowsNotSupportedException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\n\r\n";
            AssertAsync.ThrowsAsync<NotSupportedException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_TransferEncoding_TakesPrecedenceOverContentLength()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 100\r\n\r\n" +
                               "1\r\na\r\n0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(1, parsed.Body.Length);
                Assert.AreEqual("a", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_MultipleContentLength_Conflicting_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\nHey";
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_MultipleContentLength_Same_Accepted()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Length: 3\r\n\r\nHey";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("Hey", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_EmptyBody_ReturnsEmptyArray()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_TransferEncodingIdentity_TreatedAsAbsent()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: identity\r\nContent-Length: 5\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_StatusLine_NoReasonPhrase_Parses()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_StatusLine_EmptyReasonPhrase_Parses()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 \r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_StatusLine_MultiWordReasonPhrase_Parses()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 All Good Here\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ContentLength_ParsedAsLong()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ChunkedBody_LargeChunkSizeHex_ParsedAsLong()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "B2D05E00\r\n" +
                           "0\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_1xxResponses_ExceedsMaxIterations_ThrowsFormatException()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 11; i++)
                sb.Append("HTTP/1.1 100 Continue\r\n\r\n");
            sb.Append("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(sb.ToString()));
        }

        [Test]
        public void Parse_ChunkedBody_WithExtensions_StripsExtensionsBeforeParsing()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                               "4;ext=value\r\n" +
                               "Test\r\n" +
                               "0\r\n\r\n";
                var parsed = await ParseAsync(response);
                Assert.AreEqual("Test", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ChunkedBody_ExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "06400001\r\n" +
                           "0\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_ExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 104857601\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

#if TURBOHTTP_INTEGRATION_TESTS
        [Test]
        public void Parse_ReadToEnd_ExceedsMaxBodySize_ThrowsIOException()
        {
            var header = "HTTP/1.1 200 OK\r\n\r\n";
            var body = new string('a', 104857601);
            var response = header + body;
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }
#endif

        [Test]
        public void Parse_MultipleSetCookieHeaders_AllPreservedViaAdd()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\n\r\n";
                var parsed = await ParseAsync(response);
                var values = parsed.Headers.GetValues("Set-Cookie");
                Assert.AreEqual(2, values.Count);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ReadToEnd_ForcesKeepAliveFalse()        {
            Task.Run(async () =>
            {
                var response = "HTTP/1.1 200 OK\r\n\r\nHello";
                var parsed = await ParseAsync(response);
                Assert.IsFalse(parsed.KeepAlive);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_ChunkedBody_SingleChunkExceedsMaxBodySize_ThrowsIOException()
        {
            var response = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                           "06400001\r\n" +
                           "0\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_NarrowedToInt_AfterMaxBodySizeCheck()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\n";
            AssertAsync.ThrowsAsync<IOException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_Negative_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n";
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_ContentLength_NonNumeric_ThrowsFormatException()
        {
            var response = "HTTP/1.1 200 OK\r\nContent-Length: abc\r\n\r\n";
            AssertAsync.ThrowsAsync<FormatException>(async () => await ParseAsync(response));
        }

        [Test]
        public void Parse_FragmentedHeadersAcrossReads_ParsesCorrectly()
        {
            Task.Run(async () =>
            {
                var raw = "HTTP/1.1 200 OK\r\nX-One: A\r\nX-Two: B\r\nContent-Length: 5\r\n\r\nHello";
                var parsed = await ParseFragmentedAsync(Encoding.ASCII.GetBytes(raw), new[] { 2, 3, 1, 4 });

                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual("A", parsed.Headers.Get("X-One"));
                Assert.AreEqual("B", parsed.Headers.Get("X-Two"));
                Assert.AreEqual("Hello", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_SplitDelimiterBoundaries_ParsesCorrectly()
        {
            Task.Run(async () =>
            {
                var raw = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nX-Test: value\r\n\r\nOK";
                // Forces boundaries inside CR/LF delimiters.
                var parsed = await ParseFragmentedAsync(Encoding.ASCII.GetBytes(raw), new[] { 1, 1, 1, 1, 2, 1, 3 });

                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual("value", parsed.Headers.Get("X-Test"));
                Assert.AreEqual("OK", Encoding.ASCII.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_LargeHeaderBlockWithinLimit_ParsesCorrectly()
        {
            Task.Run(async () =>
            {
                var largeValue = new string('x', 3000);
                var response = "HTTP/1.1 200 OK\r\n"
                    + "X-A: " + largeValue + "\r\n"
                    + "X-B: " + largeValue + "\r\n"
                    + "X-C: " + largeValue + "\r\n"
                    + "Content-Length: 0\r\n\r\n";

                var parsed = await ParseFragmentedAsync(Encoding.ASCII.GetBytes(response), new[] { 512, 256, 128, 64 });
                Assert.AreEqual(HttpStatusCode.OK, parsed.StatusCode);
                Assert.AreEqual(0, parsed.Body.Length);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Parse_SplitMultibytePayloadBoundary_ContentLengthPathRemainsCorrect()
        {
            Task.Run(async () =>
            {
                var payload = "hello-🙂-world";
                var bodyBytes = Encoding.UTF8.GetBytes(payload);
                var header = "HTTP/1.1 200 OK\r\nContent-Length: " + bodyBytes.Length + "\r\n\r\n";
                var responseBytes = new byte[Encoding.ASCII.GetByteCount(header) + bodyBytes.Length];
                Encoding.ASCII.GetBytes(header, 0, header.Length, responseBytes, 0);
                Buffer.BlockCopy(bodyBytes, 0, responseBytes, Encoding.ASCII.GetByteCount(header), bodyBytes.Length);

                var parsed = await ParseFragmentedAsync(responseBytes, new[] { 5, 2, 1, 3, 4, 1, 2 });
                Assert.AreEqual(payload, Encoding.UTF8.GetString(parsed.Body));
            }).GetAwaiter().GetResult();
        }
    }
}
