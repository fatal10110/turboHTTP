using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// RFC 9110 Section 8.6 (#631): a method that defines request content (POST, PUT, PATCH) is
    /// framed with Content-Length: 0 when it carries none; other methods send no framing header.
    /// </summary>
    [TestFixture]
    public class Http11EmptyBodyFramingTests
    {
        [TestCase(HttpMethod.POST)]
        [TestCase(HttpMethod.PUT)]
        [TestCase(HttpMethod.PATCH)]
        public void NoBody_MethodWithContent_SendsContentLengthZero(HttpMethod method)
        {
            Task.Run(async () =>
            {
                var head = await SerializeHeadAsync(new UHttpRequest(method, new Uri("http://example.com/")));

                Assert.AreEqual(1, CountOccurrences(head, "\r\nContent-Length: 0\r\n"), head);
                Assert.AreEqual(1, CountOccurrences(head, "Content-Length:"), head);
                Assert.IsFalse(head.Contains("Transfer-Encoding:"), head);
            }).GetAwaiter().GetResult();
        }

        [TestCase(HttpMethod.POST)]
        [TestCase(HttpMethod.PUT)]
        [TestCase(HttpMethod.PATCH)]
        public void EmptyBufferedBody_MethodWithContent_SendsContentLengthZero(HttpMethod method)
        {
            Task.Run(async () =>
            {
                var request = new UHttpRequest(method, new Uri("http://example.com/"), body: Array.Empty<byte>());
                var head = await SerializeHeadAsync(request);

                Assert.AreEqual(1, CountOccurrences(head, "\r\nContent-Length: 0\r\n"), head);
                Assert.AreEqual(1, CountOccurrences(head, "Content-Length:"), head);
            }).GetAwaiter().GetResult();
        }

        [TestCase(HttpMethod.POST)]
        [TestCase(HttpMethod.PUT)]
        [TestCase(HttpMethod.PATCH)]
        public void CallerContentLengthZero_MethodWithContent_SentExactlyOnce(HttpMethod method)
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "0");
                var request = new UHttpRequest(method, new Uri("http://example.com/"), headers);

                var head = await SerializeHeadAsync(request);

                Assert.AreEqual(1, CountOccurrences(head, "\r\nContent-Length: 0\r\n"), head);
                Assert.AreEqual(1, CountOccurrences(head, "Content-Length:"), head);
            }).GetAwaiter().GetResult();
        }

        [TestCase(HttpMethod.GET)]
        [TestCase(HttpMethod.HEAD)]
        [TestCase(HttpMethod.DELETE)]
        [TestCase(HttpMethod.OPTIONS)]
        public void NoBody_MethodWithoutContent_SendsNoFramingHeader(HttpMethod method)
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "0");
                var request = new UHttpRequest(method, new Uri("http://example.com/"), headers);

                var head = await SerializeHeadAsync(request);

                Assert.IsFalse(head.Contains("Content-Length:"), head);
                Assert.IsFalse(head.Contains("Transfer-Encoding:"), head);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NonEmptyBody_Post_KeepsItsRealLength()
        {
            Task.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("Content-Length", "0");
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("http://example.com/"),
                    headers,
                    body: Encoding.ASCII.GetBytes("hello"));

                var head = await SerializeHeadAsync(request);

                Assert.AreEqual(1, CountOccurrences(head, "Content-Length:"), head);
                Assert.IsTrue(head.Contains("\r\nContent-Length: 5\r\n"), head);
            }).GetAwaiter().GetResult();
        }

        /// <summary>Returns the serialized head including the request line and the final CRLF pair.</summary>
        private static async Task<string> SerializeHeadAsync(UHttpRequest request)
        {
            using var ms = new MemoryStream();
            await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);
            var text = Encoding.ASCII.GetString(ms.ToArray());
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.GreaterOrEqual(end, 0, "The serialized request has no end of head.");
            return text.Substring(0, end + 4);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
