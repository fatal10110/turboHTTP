using System;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity;

namespace TurboHTTP.Tests.UnityModule
{
    public class AudioClipHandlerTests
    {
        [Test]
        public void AsAudioClipAsync_EmptyBody_ThrowsInvalidOperationException()
        {
            var response = CreateResponse(Array.Empty<byte>());

            AssertAsync.ThrowsAsync<InvalidOperationException>(() =>
                response.AsAudioClipAsync(AudioClipType.WAV));
        }

        [Test]
        public void AsAudioClipAsync_UnknownAudioType_ThrowsArgumentOutOfRangeException()
        {
            var response = CreateResponse(new byte[] { 0x00, 0x01, 0x02 });

            AssertAsync.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                response.AsAudioClipAsync((AudioClipType)999));
        }

        private static UHttpResponse CreateResponse(byte[] body)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "audio/wav");

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/audio"));
            return new UHttpResponse(
                HttpStatusCode.OK,
                headers,
                body,
                TimeSpan.Zero,
                request);
        }
    }
}
