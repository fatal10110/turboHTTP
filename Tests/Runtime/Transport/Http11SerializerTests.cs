using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class Http11SerializerTests
    {
        private static async Task<(string Headers, byte[] Body)> SerializeAsync(
            UHttpRequest request,
            StreamingOptions streamingOptions = null)
        {
            using var ms = new MemoryStream();
            await Http11RequestSerializer.SerializeAsync(
                request,
                ms,
                CancellationToken.None,
                streamingOptions: streamingOptions);

            return ParseSerializedBytes(ms.ToArray());
        }

        private static async Task<(string Headers, byte[] Body, Http11RequestWriteState WriteState)> SerializeWithWriteStateAsync(
            UHttpRequest request,
            StreamingOptions streamingOptions = null)
        {
            using var ms = new MemoryStream();
            var writeState = new Http11RequestWriteState();
            await Http11RequestSerializer.SerializeAsync(
                request,
                ms,
                CancellationToken.None,
                writeState,
                streamingOptions);

            var parsed = ParseSerializedBytes(ms.ToArray());
            return (parsed.Headers, parsed.Body, writeState);
        }

        private static async Task<(string Headers, byte[] Body)> SerializeInStagesAsync(
            UHttpRequest request,
            StreamingOptions streamingOptions = null)
        {
            using var ms = new MemoryStream();
            await Http11RequestSerializer.SerializeHeadersAsync(
                request,
                ms,
                CancellationToken.None);

            using var session = await request.Content.OpenReadSessionAsync(CancellationToken.None);
            await Http11RequestSerializer.SerializeBodyAsync(
                ms,
                request.Content,
                session,
                CancellationToken.None,
                streamingOptions: streamingOptions);

            return ParseSerializedBytes(ms.ToArray());
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static (string Headers, byte[] Body) ParseSerializedBytes(byte[] bytes)
        {
            var marker = new byte[] { 13, 10, 13, 10 };
            int headerEnd = IndexOf(bytes, marker);
            if (headerEnd < 0)
                return (EncodingHelper.Latin1.GetString(bytes, 0, bytes.Length), Array.Empty<byte>());

            var headerText = EncodingHelper.Latin1.GetString(bytes, 0, headerEnd);
            var body = new byte[bytes.Length - headerEnd - marker.Length];
            Buffer.BlockCopy(bytes, headerEnd + marker.Length, body, 0, body.Length);
            return (headerText, body);
        }

        [Test]
        public void SerializeGet_ProducesCorrectRequestLine()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/path"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.StartsWith("GET /path HTTP/1.1\r\n"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeGet_AutoAddsHostHeader()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Host: example.com"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeGet_NonDefaultPort_IncludesPortInHost()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com:8080/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Host: example.com:8080"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeGet_UserSetHostHeader_Preserved()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Host", "custom.example.com");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Host: custom.example.com"));
                Assert.IsFalse(result.Headers.Contains("Host: example.com"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeGet_AutoAddsConnectionKeepAlive()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Connection: keep-alive"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeGet_UserSetConnectionHeader_NotOverridden()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Connection", "close");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Connection: close"));
                Assert.IsFalse(result.Headers.Contains("Connection: keep-alive"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_AutoAddsContentLength()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    body: Encoding.UTF8.GetBytes("hello"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_WritesBody()        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"), body: body);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Body.SequenceEqual(body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_BufferedBody_TracksCommittedBodyBytes()
        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeWithWriteStateAsync(request);

                Assert.AreEqual(5, result.WriteState.BodyBytesWritten);
                Assert.IsTrue(result.WriteState.HasCommittedBodyBytes);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_KnownLengthFactoryBody_WritesContentLengthAndBody()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("stream-body");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(body, writable: false)),
                        body.Length);

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 11"));
                Assert.IsFalse(result.Headers.Contains("Transfer-Encoding:"));
                Assert.IsTrue(result.Body.SequenceEqual(body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_UnknownLengthFactoryBody_UsesChunkedTransferEncoding()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Transfer-Encoding: chunked"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
                Assert.AreEqual("5\r\nhello\r\n0\r\n\r\n", Encoding.ASCII.GetString(result.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_UnknownLengthFactoryBody_WithRequestTrailers_WritesTrailerSection()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)))
                    .WithRequestTrailers(
                        new[] { "Digest", "X-Chunk-Count" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc");
                            trailers.Set("X-Chunk-Count", "1");
                            return trailers;
                        });

                var result = await SerializeAsync(request);

                Assert.IsTrue(result.Headers.Contains("Trailer: Digest, X-Chunk-Count"));
                Assert.AreEqual(
                    "5\r\nhello\r\n0\r\nDigest: sha-256=abc\r\nX-Chunk-Count: 1\r\n\r\n",
                    Encoding.ASCII.GetString(result.Body));
            });
        }

        [Test]
        public void SerializePost_EmptyBodyWithRequestTrailers_UsesChunkedTransferEncodingAndWritesTrailerSection()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBody(ReadOnlyMemory<byte>.Empty)
                    .WithRequestTrailers(new[] { "Digest" }, CreateDigestTrailerProvider());

                var result = await SerializeAsync(request);

                Assert.IsTrue(result.Headers.Contains("Transfer-Encoding: chunked"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
                Assert.AreEqual(
                    "0\r\nDigest: sha-256=abc\r\n\r\n",
                    Encoding.ASCII.GetString(result.Body));
            });
        }

        [Test]
        public void SerializePost_KnownLengthFactoryBody_WithRequestTrailers_ThrowsInvalidOperationException()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(body, writable: false)),
                        body.Length)
                    .WithRequestTrailers(new[] { "Digest" }, CreateDigestTrailerProvider());

                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(async () => await SerializeAsync(request));
                StringAssert.Contains("chunked transfer encoding", ex.Message);
            });
        }

        [Test]
        public void SerializePost_BufferedBody_WithRequestTrailers_ThrowsInvalidOperationException()
        {
            var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                .WithBody("hello")
                .WithRequestTrailers(new[] { "Digest" }, CreateDigestTrailerProvider());

            var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(async () => await SerializeAsync(request));
            StringAssert.Contains("chunked transfer encoding", ex.Message);
        }

        [Test]
        public void SerializePost_RequestTrailers_FilterProhibitedFields_AndAllowUndeclaredFields()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("hello"), writable: false)))
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc");
                            trailers.Set("X-Extra", "ok");
                            trailers.Set("Content-Length", "999");
                            return trailers;
                        });

                var result = await SerializeAsync(request);
                var bodyText = Encoding.ASCII.GetString(result.Body);

                StringAssert.Contains("Digest: sha-256=abc\r\n", bodyText);
                StringAssert.Contains("X-Extra: ok\r\n", bodyText);
                Assert.IsFalse(bodyText.Contains("Content-Length: 999"));
            });
        }

        [Test]
        public void SerializePost_RequestTrailers_NullProviderResult_WritesPlainTerminalChunk()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("hello"), writable: false)))
                    .WithRequestTrailers(new[] { "Digest" }, () => null);

                var result = await SerializeAsync(request);
                Assert.AreEqual("5\r\nhello\r\n0\r\n\r\n", Encoding.ASCII.GetString(result.Body));
            });
        }

        [Test]
        public void SerializePost_RequestTrailers_CRLFInjection_ThrowsArgumentException()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(
                            new MemoryStream(Encoding.UTF8.GetBytes("hello"), writable: false)))
                    .WithRequestTrailers(
                        new[] { "Digest" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc\r\nInjected: bad");
                            return trailers;
                        });

                var ex = AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
                StringAssert.Contains("CRLF", ex.Message);
            });
        }

        [Test]
        public void SerializePost_UnknownLengthFactoryBody_TracksCommittedBodyBytes()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)));

                var result = await SerializeWithWriteStateAsync(request);

                Assert.AreEqual(5, result.WriteState.BodyBytesWritten);
                Assert.IsTrue(result.WriteState.HasCommittedBodyBytes);
            }).GetAwaiter().GetResult();
        }

        private static Func<HttpHeaders> CreateDigestTrailerProvider()
        {
            return () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("Digest", "sha-256=abc");
                return trailers;
            };
        }

        [Test]
        public void Serialize_MultiValueHeaders_EmitsSeparateLines()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Set-Cookie", "a=1");
                headers.Add("Set-Cookie", "b=2");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Set-Cookie: a=1"));
                Assert.IsTrue(result.Headers.Contains("Set-Cookie: b=2"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_HostHeaderWithCRLF_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Host", "bad\r\nvalue");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_AnyHeaderValueWithCRLF_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("X-Test", "bad\r\nvalue");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_HeaderNameWithColon_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Bad:Name", "value");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_HeaderNameWithCRLF_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Bad\r\nName", "value");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_HeaderNameWithSpace_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Bad Name", "value");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_HeaderNameWithAtSymbol_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Bad@Name", "value");
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_HeaderNameWithValidRfc9110TChars_AcceptsRequest()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("X-Test_!#$%&'*+-.^`|~", "value");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("X-Test_!#$%&'*+-.^`|~: value"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_NoBody_NoContentLength()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var result = await SerializeAsync(request);
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_UserSetContentLength_Mismatch_IsNormalizedToActualBodyLength()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "4");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
                Assert.IsFalse(result.Headers.Contains("Content-Length: 4"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_UserSetContentLength_Correct_Preserved()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "5");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_DuplicateContentLength_ConflictingValues_AreReplacedByTransportValue()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Content-Length", "5");
                headers.Add("Content-Length", "6");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
                Assert.IsFalse(result.Headers.Contains("Content-Length: 6"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_DuplicateContentLength_IdenticalValues_AcceptsRequest()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Add("Content-Length", "5");
                headers.Add("Content-Length", "5");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_CommaSeparatedContentLength_IdenticalValues_AcceptsRequest()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "5, 5");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
                Assert.IsFalse(result.Headers.Contains("Content-Length: 5, 5"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_CommaSeparatedContentLength_ConflictingValues_AreReplacedByTransportValue()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "5, 6");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
                Assert.IsFalse(result.Headers.Contains("Content-Length: 5, 6"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_TransferEncodingAndContentLength_OnEmptyRequest_AreStripped()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Transfer-Encoding", "gzip");
                headers.Set("Content-Length", "0");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                var result = await SerializeAsync(request);
                Assert.IsFalse(result.Headers.Contains("Transfer-Encoding:"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_AutoAddsUserAgent()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("User-Agent: TurboHTTP/1.0"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_UserSetUserAgent_NotOverridden()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("User-Agent", "Custom");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("User-Agent: Custom"));
                Assert.IsFalse(result.Headers.Contains("User-Agent: TurboHTTP/1.0"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_TransferEncodingSet_OnEmptyRequest_IsStripped()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Transfer-Encoding", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                var result = await SerializeAsync(request);
                Assert.IsFalse(result.Headers.Contains("Transfer-Encoding:"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_TransferEncodingAny_WithKnownLengthBody_IsStrippedAndContentLengthIsSet()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Transfer-Encoding", "gzip");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.UTF8.GetBytes("hello"));

                var result = await SerializeAsync(request);
                Assert.IsFalse(result.Headers.Contains("Transfer-Encoding:"));
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_ContentLengthOnUnknownLengthBody_IsStrippedAndChunkedIsApplied()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "999");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"), headers)
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("abc"), writable: false)));

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Transfer-Encoding: chunked"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
                Assert.AreEqual("3\r\nabc\r\n0\r\n\r\n", Encoding.ASCII.GetString(result.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_EmptyHeaderName_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            var field = typeof(HttpHeaders).GetField("_headers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = (System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>)field.GetValue(headers);
            dict[""] = new System.Collections.Generic.List<string> { "value" };

            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);
            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
        }

        [Test]
        public void Serialize_PathAndQuery_EmptyFallsBackToSlash()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.StartsWith("GET / HTTP/1.1\r\n"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_IPv6Host_WrappedInBrackets()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://[::1]/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Host: [::1]"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_IPv6Host_NonDefaultPort_IncludesPortAfterBrackets()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://[::1]:8080/"));
                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Host: [::1]:8080"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_ProxyAbsoluteForm_UsesAbsoluteRequestTarget()
        {
            Task.Run(async () =>
            {
                var metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    [RequestMetadataKeys.ProxyAbsoluteForm] = true
                };

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("http://example.com/api/users?x=1"),
                    metadata: metadata);

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.StartsWith("GET http://example.com/api/users?x=1 HTTP/1.1\r\n"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializeHeadersAsync_WritesHeaderBlockWithoutBody()
        {
            AssertAsync.Run(async () =>
            {
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/upload"),
                    body: Encoding.UTF8.GetBytes("hello"));

                using var stream = new MemoryStream();
                await Http11RequestSerializer.SerializeHeadersAsync(
                    request,
                    stream,
                    CancellationToken.None);

                var result = ParseSerializedBytes(stream.ToArray());
                Assert.IsTrue(result.Headers.Contains("Content-Length: 5"));
                Assert.AreEqual(0, result.Body.Length);
            });
        }

        [Test]
        public void SerializeInStages_KnownLengthFactoryBody_MatchesSerializeAsync()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("stream-body");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(
                        _ => new ValueTask<Stream>(new MemoryStream(body, writable: false)),
                        body.Length);

                var staged = await SerializeInStagesAsync(request);
                var singleShot = await SerializeAsync(request);

                Assert.AreEqual(singleShot.Headers, staged.Headers);
                CollectionAssert.AreEqual(singleShot.Body, staged.Body);
            });
        }

        [Test]
        public void SerializeInStages_ChunkedBodyWithTrailers_MatchesSerializeAsync()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("hello");
                var request = new UHttpRequest(HttpMethod.POST, new Uri("http://example.com/"))
                    .WithBodyFactory(_ => new ValueTask<Stream>(new MemoryStream(body, writable: false)))
                    .WithRequestTrailers(
                        new[] { "Digest", "X-Chunk-Count" },
                        () =>
                        {
                            var trailers = new HttpHeaders();
                            trailers.Set("Digest", "sha-256=abc");
                            trailers.Set("X-Chunk-Count", "1");
                            return trailers;
                        });

                var staged = await SerializeInStagesAsync(request);
                var singleShot = await SerializeAsync(request);

                Assert.AreEqual(singleShot.Headers, staged.Headers);
                CollectionAssert.AreEqual(singleShot.Body, staged.Body);
            });
        }

        [Test]
        public void SerializePost_SmallBufferedBodyWithinThreshold_UsesSingleWrite()
        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/submit"),
                    body: Encoding.UTF8.GetBytes("hello"));
                var options = new StreamingOptions
                {
                    SmallBufferedRequestThresholdBytes = 16
                };

                using var stream = new CountingWriteStream();
                await Http11RequestSerializer.SerializeAsync(
                    request,
                    stream,
                    CancellationToken.None,
                    streamingOptions: options);

                var result = ParseSerializedBytes(stream.ToArray());
                Assert.AreEqual(1, stream.WriteCount);
                Assert.AreEqual("hello", Encoding.UTF8.GetString(result.Body));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SerializePost_BufferedBodyAboveThreshold_UsesSeparateHeaderAndBodyWrites()
        {
            Task.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("buffered-body");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/submit"),
                    body: body);
                var options = new StreamingOptions
                {
                    SmallBufferedRequestThresholdBytes = 4
                };

                using var stream = new CountingWriteStream();
                await Http11RequestSerializer.SerializeAsync(
                    request,
                    stream,
                    CancellationToken.None,
                    streamingOptions: options);

                var result = ParseSerializedBytes(stream.ToArray());
                Assert.AreEqual(2, stream.WriteCount);
                Assert.IsTrue(result.Body.SequenceEqual(body));
            }).GetAwaiter().GetResult();
        }

        private sealed class CountingWriteStream : Stream
        {
            private readonly MemoryStream _inner = new MemoryStream();

            public int WriteCount { get; private set; }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public byte[] ToArray() => _inner.ToArray();

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                _inner.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteCount++;
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                WriteCount++;
                _inner.Write(buffer);
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                WriteCount++;
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                WriteCount++;
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
