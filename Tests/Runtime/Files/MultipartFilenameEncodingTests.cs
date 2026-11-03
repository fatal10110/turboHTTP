using System;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Files;

namespace TurboHTTP.Tests.Files
{
    /// <summary>
    /// #154: non-ASCII file names get an ASCII filename= fallback plus an RFC 8187 filename*.
    /// </summary>
    public class MultipartFilenameEncodingTests
    {
        private static string BuildWithFile(string filename)
        {
            var builder = new MultipartFormDataBuilder("enc");
            builder.AddFile("f", filename, new byte[] { 1 });
            return Encoding.UTF8.GetString(builder.Build());
        }

        [Test]
        public void AsciiFilename_EmitsQuotedFilenameOnly()
        {
            var text = BuildWithFile("report 1.txt");

            Assert.That(text, Does.Contain("Content-Disposition: form-data; name=\"f\"; filename=\"report 1.txt\"\r\n"));
            Assert.That(text, Does.Not.Contain("filename*"));
        }

        [Test]
        public void Latin1Filename_EmitsAsciiFallbackAndUtf8ExtValue()
        {
            var text = BuildWithFile("café résumé.pdf");

            Assert.That(text, Does.Contain(
                "name=\"f\"; filename=\"caf_ r_sum_.pdf\"; filename*=UTF-8''caf%C3%A9%20r%C3%A9sum%C3%A9.pdf\r\n"));
        }

        [Test]
        public void CjkFilename_EmitsAsciiFallbackAndUtf8ExtValue()
        {
            var text = BuildWithFile("日本語.txt");

            Assert.That(text, Does.Contain(
                "filename=\"___.txt\"; filename*=UTF-8''%E6%97%A5%E6%9C%AC%E8%AA%9E.txt\r\n"));
        }

        [Test]
        public void SurrogatePair_IsOneFallbackCharacter_AndFourUtf8Bytes()
        {
            var text = BuildWithFile("a\U0001F600.png");

            Assert.That(text, Does.Contain("filename=\"a_.png\"; filename*=UTF-8''a%F0%9F%98%80.png\r\n"));
        }

        [Test]
        public void NonAsciiFilename_WithQuoteAndBackslash_EscapesFallbackAndPercentEncodesExtValue()
        {
            var text = BuildWithFile("é\"x\\y.txt");

            Assert.That(text, Does.Contain("filename=\"_\\\"x\\\\y.txt\"; filename*=UTF-8''%C3%A9%22x%5Cy.txt\r\n"));
        }

        [Test]
        public void NonAsciiFilename_WithCrLf_IsStillRejected()
        {
            var builder = new MultipartFormDataBuilder("enc");
            Assert.Throws<ArgumentException>(() => builder.AddFile("f", "日\r\nX-Evil: 1.txt", new byte[0]));
            Assert.Throws<ArgumentException>(() => builder.AddFile("f", "café\n.txt", new byte[0]));
        }
    }
}
