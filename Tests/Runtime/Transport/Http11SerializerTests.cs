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
        private static async Task<(string Headers, byte[] Body)> SerializeAsync(UHttpRequest request)
        {
            using var ms = new MemoryStream();
            await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);

            var bytes = ms.ToArray();
            var marker = new byte[] { 13, 10, 13, 10 };
            int headerEnd = IndexOf(bytes, marker);
            if (headerEnd < 0)
                return (EncodingHelper.Latin1.GetString(bytes, 0, bytes.Length), Array.Empty<byte>());

            var headerText = EncodingHelper.Latin1.GetString(bytes, 0, headerEnd);
            var body = new byte[bytes.Length - headerEnd - marker.Length];
            Buffer.BlockCopy(bytes, headerEnd + marker.Length, body, 0, body.Length);
            return (headerText, body);
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
        public void Serialize_NoBody_NoContentLength()        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"));
                var result = await SerializeAsync(request);
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_UserSetContentLength_Mismatch_ThrowsArgumentException()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Length", "4");
            var request = new UHttpRequest(
                HttpMethod.POST,
                new Uri("http://example.com/"),
                headers,
                body: Encoding.UTF8.GetBytes("hello"));

            AssertAsync.ThrowsAsync<ArgumentException>(async () => await SerializeAsync(request));
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
        public void Serialize_TransferEncodingSet_NoAutoContentLength()        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Transfer-Encoding", "gzip");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                var result = await SerializeAsync(request);
                Assert.IsTrue(result.Headers.Contains("Transfer-Encoding: gzip"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Serialize_TransferEncodingAny_WithBody_PreservesTransferEncoding()
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
                Assert.IsTrue(result.Headers.Contains("Transfer-Encoding: gzip"));
                Assert.IsFalse(result.Headers.Contains("Content-Length:"));
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
    }
}
