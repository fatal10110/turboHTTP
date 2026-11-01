using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// The shared request field validator (RFC 9110 Sections 5.1 and 5.5, RFC 9113 Section 8.2.1)
    /// and its use by the HTTP/1.1 serializer for headers and trailers (#49, #26).
    /// </summary>
    [TestFixture]
    public class HttpFieldValidatorTests
    {
        [TestCase("bad\r\nInjected: yes", "CRLF")]
        [TestCase("bad\nvalue", "CRLF")]
        [TestCase("nul\0value", "0x00")]
        [TestCase("ctl\u0001value", "0x01")]
        [TestCase("ctl\u0008value", "0x08")]
        [TestCase("ctl\u000Bvalue", "0x0B")]
        [TestCase("ctl\u001Fvalue", "0x1F")]
        [TestCase("del\u007Fvalue", "0x7F")]
        [TestCase("wideĀvalue", "U+0100")]
        [TestCase(" leading", "whitespace")]
        [TestCase("\tleading", "whitespace")]
        [TestCase("trailing ", "whitespace")]
        [TestCase("trailing\t", "whitespace")]
        public void ValidateField_InvalidValue_Throws(string value, string expectedInMessage)
        {
            var ex = Assert.Throws<ArgumentException>(() => HttpFieldValidator.ValidateField("X-Test", value));
            StringAssert.Contains(expectedInMessage, ex.Message);
            StringAssert.Contains("X-Test", ex.Message);
        }

        [TestCase(":path")]
        [TestCase(":authority")]
        [TestCase("X Test")]
        [TestCase("X-Test:")]
        [TestCase("X-Test\r\nInjected")]
        [TestCase("X-Test\0")]
        [TestCase("X-Tést")]
        [TestCase("")]
        [TestCase(null)]
        public void ValidateField_InvalidName_Throws(string name)
        {
            Assert.Throws<ArgumentException>(() => HttpFieldValidator.ValidateField(name, "ok"));
        }

        [Test]
        public void ValidateField_InvalidName_DoesNotEchoTheName()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => HttpFieldValidator.ValidateField("X-Test\r\nAuthorization: Bearer secret", "ok"));
            StringAssert.DoesNotContain("secret", ex.Message);
        }

        [TestCase("X-Test", "")]
        [TestCase("X-Test", null)]
        [TestCase("X-Test", "a\tb")]
        [TestCase("X-Test", "a b")]
        [TestCase("X-Test", "café ÿ\u0080")]
        [TestCase("X-Test", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")]
        [TestCase("Authorization", "Bearer abc.def")]
        [TestCase("x-lower", "v")]
        [TestCase("!#$%&'*+-.^_`|~09AZaz", "v")]
        public void ValidateField_ValidField_DoesNotThrow(string name, string value)
        {
            Assert.DoesNotThrow(() => HttpFieldValidator.ValidateField(name, value));
        }

        [Test]
        public void ValidateFields_ChecksEveryValueOfEveryName()
        {
            var headers = new HttpHeaders();
            headers.Add("X-Multi", "fine");
            headers.Add("X-Multi", "bad\0");

            var ex = Assert.Throws<ArgumentException>(() => HttpFieldValidator.ValidateFields(headers));
            StringAssert.Contains("0x00", ex.Message);
            Assert.DoesNotThrow(() => HttpFieldValidator.ValidateFields(null));
        }

        // --- HTTP/1.1 serializer uses the shared validator (#49) ---

        [TestCase("nul\0value")]
        [TestCase("ctl\u0001value")]
        [TestCase("ctl\u001Fvalue")]
        [TestCase("del\u007Fvalue")]
        [TestCase("trailing ")]
        public void Http11Serialize_HeaderValueWithCtlOrSurroundingWhitespace_ThrowsArgumentException(string value)
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("X-Test", value);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                using var ms = new MemoryStream();
                AssertAsync.ThrowsAsync<ArgumentException>(
                    async () => await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None));
                Assert.AreEqual(0, ms.Length, "nothing may be written for an invalid request");
            });
        }

        [Test]
        public void Http11Serialize_PseudoHeaderName_ThrowsArgumentException()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set(":authority", "evil.example.com");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                using var ms = new MemoryStream();
                AssertAsync.ThrowsAsync<ArgumentException>(
                    async () => await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None));
            });
        }

        [Test]
        public void Http11Serialize_HtabAndObsTextInValue_AreSent()
        {
            AssertAsync.Run(async () =>
            {
                var headers = new HttpHeaders();
                headers.Set("X-Test", "a\tb café");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("http://example.com/"), headers);

                using var ms = new MemoryStream();
                await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);
                var wire = EncodingHelper.Latin1.GetString(ms.ToArray());
                StringAssert.Contains("X-Test: a\tb café\r\n", wire);
            });
        }

        [Test]
        public void Http11Serialize_RequestTrailerValueWithNul_ThrowsArgumentException()
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
                            trailers.Set("Digest", "sha-256=abc\0");
                            return trailers;
                        });

                using var ms = new MemoryStream();
                var ex = AssertAsync.ThrowsAsync<ArgumentException>(
                    async () => await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None));
                StringAssert.Contains("0x00", ex.Message);
            });
        }
    }
}
