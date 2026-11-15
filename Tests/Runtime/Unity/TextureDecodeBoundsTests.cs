using System;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Pre-decode bounds of <see cref="Texture2DHandler"/> (#999, #1000): the header guard rejects oversized or unreadable images before Texture2D.LoadImage runs.
    /// </summary>
    public class TextureDecodeBoundsTests
    {
        [Test]
        public void TextureOptions_Defaults_AreFinite()
        {
            var options = new TextureOptions();

            Assert.AreEqual(4096L * 4096, options.MaxPixels);
            Assert.AreEqual(TextureOptions.DefaultMaxPixels, options.MaxPixels);
            Assert.AreEqual(TextureOptions.DefaultMaxSourceBytes, options.MaxSourceBytes);
            Assert.AreEqual(TextureOptions.DefaultMaxSourceBytes, options.MaxBodyBytes);
        }

        [Test]
        public void TextureOptions_MaxSourceBytesAndMaxBodyBytes_AreOneLimit()
        {
            var options = new TextureOptions { MaxBodyBytes = 1234 };
            Assert.AreEqual(1234, options.MaxSourceBytes);

            options.MaxSourceBytes = null;
            Assert.IsNull(options.MaxBodyBytes, "null is the explicit no-limit opt-out");
        }

        [Test]
        public void AsTexture2D_DefaultOptions_RejectsPngHeaderDeclaring16384Squared()
        {
            var response = CreateResponse(CreatePngHeader(16384, 16384), "image/png");

            var ex = Assert.Throws<InvalidOperationException>(() => response.AsTexture2D());

            StringAssert.Contains("MaxPixels", ex.Message);
            StringAssert.DoesNotContain("LoadImage", ex.Message);
        }

        [Test]
        public void AsTexture2D_JpegWithFillByteBeforeSof0_RejectedWithoutLoadImage()
        {
            var response = CreateResponse(CreateJpegHeader(8192, 8192, fillBytes: 1), "image/jpeg");

            var ex = Assert.Throws<InvalidOperationException>(() => response.AsTexture2D());

            StringAssert.Contains("MaxPixels", ex.Message);
            StringAssert.DoesNotContain("LoadImage", ex.Message);
        }

        [Test]
        public void AsTexture2D_UnknownDimensions_FailsClosedWhileMaxPixelsIsSet()
        {
            // GIF header: not header-probed, so the size cannot be established before decode.
            var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x40, 0x00, 0x40, 0x00, 0x00, 0x00 };
            var response = CreateResponse(gif, "image/gif");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                response.AsTexture2D(new TextureOptions { MaxPixels = 1_000_000 }));

            StringAssert.Contains("MaxPixels", ex.Message);
            StringAssert.DoesNotContain("LoadImage", ex.Message);
        }

        [Test]
        public void AsTexture2D_MaxPixelsNull_SkipsHeaderRequirement()
        {
            var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
            var response = CreateResponse(gif, "image/gif");

            // With the opt-out the payload reaches LoadImage, which rejects a GIF.
            LogAssert.ignoreFailingMessages = true;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                response.AsTexture2D(new TextureOptions { MaxPixels = null }));

            StringAssert.DoesNotContain("MaxPixels", ex.Message);
        }

        [Test]
        public void TryReadJpegDimensions_SkipsFillBytesAndLengthlessMarkers()
        {
            var bytes = new byte[]
            {
                0xFF, 0xD8,                         // SOI
                0xFF, 0x01,                         // TEM (no length)
                0xFF, 0xFF, 0xFF, 0xD3,             // fill bytes, then RST3 (no length)
                0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, // APP0, 2 payload bytes
                0xFF, 0xFF, 0xC2,                   // fill byte, then SOF2
                0x00, 0x0B, 0x08, 0x01, 0x2C, 0x02, 0x58, 0x01, 0x01, 0x11, 0x00
            };

            Assert.IsTrue(Texture2DHandler.TryReadJpegDimensions(bytes, out var width, out var height));
            Assert.AreEqual(600, width);
            Assert.AreEqual(300, height);
        }

        [Test]
        public void TryReadJpegDimensions_TruncatedOrSosBeforeSof_ReturnsFalse()
        {
            var sosFirst = new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 };
            Assert.IsFalse(Texture2DHandler.TryReadJpegDimensions(sosFirst, out _, out _));

            var truncatedSof = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x20 };
            Assert.IsFalse(Texture2DHandler.TryReadJpegDimensions(truncatedSof, out _, out _));
        }

        [Test]
        public void TryReadPngDimensions_NegativeDimension_ReturnsFalse()
        {
            var png = CreatePngHeader(16, 16);
            png[16] = 0x80; // width >= 2^31 reads negative

            Assert.IsFalse(Texture2DHandler.TryReadPngDimensions(png, out _, out _));
        }

        private static byte[] CreatePngHeader(int width, int height)
        {
            var bytes = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
            bytes[11] = 13; // IHDR length
            bytes[12] = 0x49;
            bytes[13] = 0x48;
            bytes[14] = 0x44;
            bytes[15] = 0x52;
            WriteInt32BigEndian(bytes, 16, width);
            WriteInt32BigEndian(bytes, 20, height);
            bytes[24] = 8; // bit depth
            bytes[25] = 6; // RGBA
            return bytes;
        }

        private static byte[] CreateJpegHeader(int width, int height, int fillBytes)
        {
            var bytes = new byte[4 + fillBytes + 2 + 17];
            var i = 0;
            bytes[i++] = 0xFF;
            bytes[i++] = 0xD8; // SOI
            for (var f = 0; f < fillBytes; f++)
                bytes[i++] = 0xFF;
            bytes[i++] = 0xFF;
            bytes[i++] = 0xC0; // SOF0
            bytes[i++] = 0x00;
            bytes[i++] = 0x11; // Lf = 17
            bytes[i++] = 0x08; // P
            bytes[i++] = (byte)(height >> 8);
            bytes[i++] = (byte)height;
            bytes[i++] = (byte)(width >> 8);
            bytes[i++] = (byte)width;
            bytes[i++] = 0x03; // Nf; component specs stay zero
            return bytes;
        }

        private static void WriteInt32BigEndian(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static UHttpResponse CreateResponse(byte[] body, string contentType)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", contentType);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/image"));
            return new UHttpResponse(HttpStatusCode.OK, headers, body, TimeSpan.Zero, request);
        }
    }
}
