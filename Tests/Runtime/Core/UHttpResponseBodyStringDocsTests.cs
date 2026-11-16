using System;
using System.Net;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Pins what APIReference.md documents for <c>GetBodyAsString</c> (#588): the parameterless
    /// overload always decodes UTF-8 and ignores the charset; <c>GetContentEncoding()</c> honors it.
    /// </summary>
    [TestFixture]
    public class UHttpResponseBodyStringDocsTests
    {
        private static UHttpResponse Create(HttpHeaders headers, byte[] body) =>
            new UHttpResponse(
                HttpStatusCode.OK,
                headers,
                body,
                TimeSpan.Zero,
                new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/")));

        [Test]
        public void GetBodyAsString_IgnoresCharset_AndContentEncodingDecodesIt()
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "text/plain; charset=ISO-8859-1");
            var latin1 = Encoding.GetEncoding("ISO-8859-1");
            using var response = Create(headers, latin1.GetBytes("café"));

            Assert.That(response.GetBodyAsString(), Does.Contain("�"));
            Assert.AreEqual("café", response.GetBodyAsString(response.GetContentEncoding()));
        }

        [Test]
        public void GetBodyAsString_EmptyBody_ReturnsNull_ForBothOverloads()
        {
            using var response = Create(new HttpHeaders(), Array.Empty<byte>());

            Assert.IsNull(response.GetBodyAsString());
            Assert.IsNull(response.GetBodyAsString(Encoding.UTF8));
        }
    }
}
