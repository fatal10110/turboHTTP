using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class TexturePipelineV2Tests
    {
        [Test]
        public void AsTexture2D_MaxSourceBytes_RejectsOversizedPayload()
        {
            var png = CreatePngBytes(2, 2);
            var response = CreateResponse(png, "image/png");

            var options = new TextureOptions
            {
                MaxSourceBytes = 1
            };

            Assert.Throws<InvalidOperationException>(() => response.AsTexture2D(options));
        }

        [Test]
        public void AsTexture2D_MaxPixels_RejectsOversizedImage()
        {
            var png = CreatePngBytes(4, 4);
            var response = CreateResponse(png, "image/png");

            var options = new TextureOptions
            {
                MaxPixels = 4
            };

            Assert.Throws<InvalidOperationException>(() => response.AsTexture2D(options));
        }

        [UnityTest]
        public System.Collections.IEnumerator GetTextureAsync_ThreadedPathFallback_UsesUnityDecode()
        {
            var png = CreatePngBytes(2, 2);
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "image/png");

            var transport = new MockTransport();
            transport.EnqueueResponse(HttpStatusCode.OK, headers, png);

            using var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true
            });

            var task = client.GetTextureAsync(
                "https://example.test/texture",
                new TextureOptions
                {
                    EnableThreadedDecode = true,
                    ThreadedDecodeMinBytes = 1
                });

            yield return new WaitUntil(() => task.IsCompleted);

            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.IsNotNull(task.Result);

            UnityEngine.Object.DestroyImmediate(task.Result);
        }

        private static UHttpResponse CreateResponse(
            byte[] body,
            string contentType,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", contentType);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/image"));
            return new UHttpResponse(
                statusCode,
                headers,
                body,
                TimeSpan.Zero,
                request);
        }

        private static byte[] CreatePngBytes(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    texture.SetPixel(x, y, ((x + y) & 1) == 0 ? Color.white : Color.black);
                }
            }

            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }
    }
}
