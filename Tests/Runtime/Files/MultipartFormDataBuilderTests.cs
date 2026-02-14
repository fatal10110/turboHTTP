using System;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Files;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Files
{
    public class MultipartFormDataBuilderTests
    {
        [Test]
        public void Build_SingleTextField_ProducesValidMultipart()
        {
            var builder = new MultipartFormDataBuilder("testboundary");
            builder.AddField("username", "john");

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            Assert.That(text, Does.Contain("--testboundary\r\n"));
            Assert.That(text, Does.Contain("Content-Disposition: form-data; name=\"username\""));
            Assert.That(text, Does.Contain("john"));
            Assert.That(text, Does.Contain("--testboundary--\r\n"));
        }

        [Test]
        public void Build_MultipleFields_AllPresent()
        {
            var builder = new MultipartFormDataBuilder("bound");
            builder.AddField("a", "1");
            builder.AddField("b", "2");
            builder.AddField("c", "3");

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            Assert.That(text, Does.Contain("name=\"a\""));
            Assert.That(text, Does.Contain("name=\"b\""));
            Assert.That(text, Does.Contain("name=\"c\""));
        }

        [Test]
        public void Build_FileField_IncludesFilenameAndContentType()
        {
            var builder = new MultipartFormDataBuilder("fileboundary");
            var fileData = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
            builder.AddFile("avatar", "photo.png", fileData, ContentTypes.Png);

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            Assert.That(text, Does.Contain("name=\"avatar\""));
            Assert.That(text, Does.Contain("filename=\"photo.png\""));
            Assert.That(text, Does.Contain("Content-Type: image/png"));
        }

        [Test]
        public void Build_FileField_ContainsFileBytes()
        {
            var builder = new MultipartFormDataBuilder("fb");
            var fileData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            builder.AddFile("data", "test.bin", fileData);

            var body = builder.Build();

            // Verify the file data bytes are present in the output
            var pattern = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            Assert.IsTrue(ContainsSubarray(body, pattern),
                "File data bytes not found in multipart body");
        }

        [Test]
        public void Build_MixedFieldsAndFiles_Correct()
        {
            var builder = new MultipartFormDataBuilder("mixed");
            builder.AddField("description", "My upload");
            builder.AddFile("file", "doc.pdf", new byte[] { 1, 2, 3 }, ContentTypes.Pdf);
            builder.AddField("tags", "important");

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            // All parts present
            Assert.That(text, Does.Contain("name=\"description\""));
            Assert.That(text, Does.Contain("name=\"file\""));
            Assert.That(text, Does.Contain("name=\"tags\""));

            // Proper boundary structure
            var boundaryCount = Regex.Matches(text, "--mixed\r\n").Count;
            Assert.AreEqual(3, boundaryCount, "Should have 3 part boundaries");

            Assert.That(text, Does.Contain("--mixed--\r\n"));
        }

        [Test]
        public void GetContentType_IncludesBoundary()
        {
            var builder = new MultipartFormDataBuilder("myboundary123");

            Assert.AreEqual("multipart/form-data; boundary=myboundary123",
                builder.GetContentType());
        }

        [Test]
        public void Boundary_IsAccessible()
        {
            var builder = new MultipartFormDataBuilder("custom");
            Assert.AreEqual("custom", builder.Boundary);
        }

        [Test]
        public void DefaultConstructor_GeneratesUniqueBoundary()
        {
            var b1 = new MultipartFormDataBuilder();
            var b2 = new MultipartFormDataBuilder();

            Assert.IsNotEmpty(b1.Boundary);
            Assert.AreNotEqual(b1.Boundary, b2.Boundary);
        }

        [Test]
        public void AddField_NullName_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentNullException>(() => builder.AddField(null, "val"));
        }

        [Test]
        public void AddFile_NullName_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentNullException>(
                () => builder.AddFile(null, "f.txt", new byte[0]));
        }

        [Test]
        public void AddFile_NullFilename_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentNullException>(
                () => builder.AddFile("f", null, new byte[0]));
        }

        [Test]
        public void AddFile_NullData_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentNullException>(
                () => builder.AddFile("f", "f.txt", null));
        }

        [Test]
        public void ApplyTo_SetsBodyAndContentType()
        {
            var builder = new MultipartFormDataBuilder("applybnd");
            builder.AddField("key", "value");

            var transport = new MockTransport();
            var client = new UHttpClient(new UHttpClientOptions { Transport = transport });
            var requestBuilder = client.Post("https://test.com/upload");
            builder.ApplyTo(requestBuilder);

            var request = requestBuilder.Build();

            Assert.AreEqual("multipart/form-data; boundary=applybnd",
                request.Headers.Get("Content-Type"));
            Assert.IsNotNull(request.Body);
            Assert.Greater(request.Body.Length, 0);
        }

        [Test]
        public void ApplyTo_NullBuilder_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentNullException>(() => builder.ApplyTo(null));
        }

        [Test]
        public void Build_EmptyParts_ProducesOnlyEndBoundary()
        {
            var builder = new MultipartFormDataBuilder("empty");
            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            Assert.AreEqual("--empty--\r\n", text);
        }

        [Test]
        public void FluentChaining_Works()
        {
            var builder = new MultipartFormDataBuilder("chain")
                .AddField("a", "1")
                .AddField("b", "2")
                .AddFile("f", "file.txt", new byte[] { 65 });

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            Assert.That(text, Does.Contain("name=\"a\""));
            Assert.That(text, Does.Contain("name=\"b\""));
            Assert.That(text, Does.Contain("name=\"f\""));
        }

        // --- Security: CRLF injection prevention ---

        [Test]
        public void AddField_NameWithCR_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentException>(
                () => builder.AddField("bad\rname", "val"));
        }

        [Test]
        public void AddField_NameWithLF_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentException>(
                () => builder.AddField("bad\nname", "val"));
        }

        [Test]
        public void AddFile_FilenameWithCRLF_Throws()
        {
            var builder = new MultipartFormDataBuilder();
            Assert.Throws<ArgumentException>(
                () => builder.AddFile("f", "bad\r\nfile.txt", new byte[0]));
        }

        [Test]
        public void AddFile_NameContainingQuote_IsEscaped()
        {
            var builder = new MultipartFormDataBuilder("qtest");
            builder.AddFile("f", "file\"name.txt", new byte[] { 1 });

            var body = builder.Build();
            var text = Encoding.UTF8.GetString(body);

            // Quote must be escaped in the output
            Assert.That(text, Does.Contain("filename=\"file\\\"name.txt\""));
        }

        // --- Boundary validation ---

        [Test]
        public void Constructor_EmptyBoundary_Throws()
        {
            Assert.Throws<ArgumentException>(() => new MultipartFormDataBuilder(""));
        }

        [Test]
        public void Constructor_BoundaryTooLong_Throws()
        {
            var longBoundary = new string('a', 71);
            Assert.Throws<ArgumentException>(() => new MultipartFormDataBuilder(longBoundary));
        }

        [Test]
        public void Constructor_BoundaryWithInvalidChars_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => new MultipartFormDataBuilder("bad;boundary"));
        }

        [Test]
        public void Constructor_ValidBoundaryChars_Works()
        {
            // RFC 2046 bchars: alphanumeric + '()+_,-./:=?
            var builder = new MultipartFormDataBuilder("abc-123_test");
            Assert.AreEqual("abc-123_test", builder.Boundary);
        }

        // --- Helper ---

        private static bool ContainsSubarray(byte[] source, byte[] pattern)
        {
            for (int i = 0; i <= source.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
