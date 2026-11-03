using System;
using System.Text;
using NUnit.Framework;
using TurboHTTP.Files;

namespace TurboHTTP.Tests.Files
{
    /// <summary>
    /// #154: non-ASCII file names are sent as UTF-8 inside the quoted filename= parameter
    /// (RFC 7578 sections 4.2 and 5.1.1); filename* is never sent (RFC 7578 section 4.2: MUST NOT).
    /// </summary>
    public class MultipartFilenameEncodingTests
    {
        private static byte[] BuildWithFile(string filename)
        {
            var builder = new MultipartFormDataBuilder("enc");
            builder.AddFile("f", filename, new byte[] { 1 });
            return builder.Build();
        }

        private static void AssertContainsBytes(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return;
            }

            Assert.Fail("expected byte sequence not found: " + Encoding.UTF8.GetString(needle));
        }

        private static void AssertUtf8Filename(string filename, string expectedQuotedValue)
        {
            var body = BuildWithFile(filename);
            AssertContainsBytes(body, Encoding.UTF8.GetBytes(
                "Content-Disposition: form-data; name=\"f\"; filename=\"" + expectedQuotedValue + "\"\r\n"));
            Assert.That(Encoding.UTF8.GetString(body), Does.Not.Contain("filename*"));
        }

        [Test]
        public void AsciiFilename_EmitsQuotedFilenameOnly()
        {
            AssertUtf8Filename("report 1.txt", "report 1.txt");
        }

        [Test]
        public void Latin1Filename_IsSentAsUtf8InQuotedFilename()
        {
            AssertUtf8Filename("café résumé.pdf", "café résumé.pdf");

            // é is the two UTF-8 bytes C3 A9, not a single Latin-1 byte or a replacement.
            AssertContainsBytes(BuildWithFile("café.pdf"), new byte[] { (byte)'f', 0xC3, 0xA9, (byte)'.' });
        }

        [Test]
        public void CjkFilename_IsSentAsUtf8InQuotedFilename()
        {
            AssertUtf8Filename("日本語.txt", "日本語.txt");
            AssertContainsBytes(BuildWithFile("日本語.txt"), new byte[]
            {
                (byte)'"', 0xE6, 0x97, 0xA5, 0xE6, 0x9C, 0xAC, 0xE8, 0xAA, 0x9E, (byte)'.'
            });
        }

        [Test]
        public void SurrogatePair_IsFourUtf8Bytes()
        {
            AssertUtf8Filename("a\U0001F600.png", "a\U0001F600.png");
            AssertContainsBytes(BuildWithFile("a\U0001F600.png"), new byte[]
            {
                (byte)'a', 0xF0, 0x9F, 0x98, 0x80, (byte)'.'
            });
        }

        [Test]
        public void NonAsciiFilename_WithQuoteAndBackslash_IsQuotedStringEscaped()
        {
            AssertUtf8Filename("é\"x\\y.txt", "é\\\"x\\\\y.txt");
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
