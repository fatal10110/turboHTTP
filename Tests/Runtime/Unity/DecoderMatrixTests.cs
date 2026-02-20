using NUnit.Framework;
using TurboHTTP.Unity.Decoders;

namespace TurboHTTP.Tests.UnityModule
{
    public class DecoderMatrixTests
    {
        [Test]
        public void DecoderRegistry_ResolvesWavDecoder()
        {
            DecoderRegistry.BootstrapDefaults();

            var resolved = DecoderRegistry.TryResolveAudioDecoder(
                "audio/wav",
                "clip.wav",
                out var decoder,
                out var reason);

            Assert.IsTrue(resolved, reason);
            Assert.IsNotNull(decoder);
            StringAssert.Contains("wav", decoder.Id.ToLowerInvariant());
        }

        [Test]
        public void DecoderRegistry_ImageDecoderResolution_TracksProviderAvailability()
        {
            DecoderRegistry.BootstrapDefaults();

            var probe = new StbImageSharpDecoder();
            var expectedCanDecode = probe.CanDecode("image/png", ".png");

            var resolved = DecoderRegistry.TryResolveImageDecoder(
                "image/png",
                "image.png",
                out var decoder,
                out var reason);

            Assert.AreEqual(expectedCanDecode, resolved);
            if (expectedCanDecode)
            {
                Assert.IsNotNull(decoder);
                Assert.IsNull(reason);
            }
            else
            {
                Assert.IsNull(decoder);
                Assert.AreEqual("no-decoder", reason);
            }
        }
    }
}
