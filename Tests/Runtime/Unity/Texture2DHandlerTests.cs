using System;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity;
using UnityEngine;

namespace TurboHTTP.Tests.UnityModule
{
    public class Texture2DHandlerTests
    {
        [Test]
        public void AsTexture2D_WithValidPngBytes_ReturnsTexture()
        {
            var pngBytes = CreatePngBytes();
            var response = CreateResponse(pngBytes, "image/png");

            var texture = response.AsTexture2D();

            Assert.IsNotNull(texture);
            Assert.AreEqual(2, texture.width);
            Assert.AreEqual(2, texture.height);

            UnityEngine.Object.DestroyImmediate(texture);
        }

        [Test]
        public void AsTexture2D_DefaultContentTypeValidation_RejectsNonImageContentType()
        {
            var pngBytes = CreatePngBytes();
            var response = CreateResponse(pngBytes, "application/octet-stream");

            var ex = Assert.Throws<InvalidOperationException>(() => response.AsTexture2D());

            StringAssert.Contains("Content-Type", ex.Message);
        }

        [Test]
        public void AsTexture2D_AllowsNonImageContentType_WhenValidationDisabled()
        {
            var pngBytes = CreatePngBytes();
            var response = CreateResponse(pngBytes, "application/octet-stream");
            var options = new TextureOptions
            {
                ValidateImageContentType = false
            };

            var texture = response.AsTexture2D(options);

            Assert.IsNotNull(texture);
            Assert.AreEqual(2, texture.width);
            Assert.AreEqual(2, texture.height);

            UnityEngine.Object.DestroyImmediate(texture);
        }

        [Test]
        public void AsTexture2D_MaxBodyBytesGuard_RejectsOversizedPayload()
        {
            var pngBytes = CreatePngBytes();
            var response = CreateResponse(pngBytes, "image/png");
            var options = new TextureOptions
            {
                MaxBodyBytes = 1
            };

            Assert.Throws<InvalidOperationException>(() => response.AsTexture2D(options));
        }

        [Test]
        public void AsSprite_CreatesSpriteFromTexture()
        {
            var pngBytes = CreatePngBytes();
            var response = CreateResponse(pngBytes, "image/png");
            var texture = response.AsTexture2D(new TextureOptions { Readable = true });

            var sprite = texture.AsSprite();

            Assert.IsNotNull(sprite);
            Assert.AreEqual(2f, sprite.rect.width);
            Assert.AreEqual(2f, sprite.rect.height);

            UnityEngine.Object.DestroyImmediate(sprite);
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static UHttpResponse CreateResponse(byte[] body, string contentType)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", contentType);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/image"));
            return new UHttpResponse(
                HttpStatusCode.OK,
                headers,
                body,
                TimeSpan.Zero,
                request);
        }

        private static byte[] CreatePngBytes()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.red);
            texture.SetPixel(1, 0, Color.green);
            texture.SetPixel(0, 1, Color.blue);
            texture.SetPixel(1, 1, Color.white);
            texture.Apply();

            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }
    }
}
