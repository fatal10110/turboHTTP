using System;
using System.Buffers;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Source and decoded-size bounds of <see cref="AudioClipHandler"/> (#953), enforced before a
    /// decode slot, temp file or decoded buffer is allocated.
    /// </summary>
    public class AudioClipDecodeBoundsTests
    {
        private AudioClipPipelineOptions _original;

        [SetUp]
        public void SetUp()
        {
            _original = AudioClipHandler.GetOptions();
        }

        [TearDown]
        public void TearDown()
        {
            AudioClipHandler.Configure(_original);
        }

        [Test]
        public void Options_Defaults_AreFiniteAndCloned()
        {
            var options = new AudioClipPipelineOptions();
            Assert.AreEqual(AudioClipPipelineOptions.DefaultMaxSourceBytes, options.MaxSourceBytes);
            Assert.AreEqual(AudioClipPipelineOptions.DefaultMaxDecodedSamples, options.MaxDecodedSamples);

            var custom = new AudioClipPipelineOptions { MaxSourceBytes = 10, MaxDecodedSamples = null }.Clone();
            Assert.AreEqual(10, custom.MaxSourceBytes);
            Assert.IsNull(custom.MaxDecodedSamples);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AudioClipPipelineOptions { MaxSourceBytes = 0 }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AudioClipPipelineOptions { MaxDecodedSamples = 0 }.Validate());
        }

        [Test]
        public void AsAudioClipAsync_SourceOverMaxSourceBytes_RejectedBeforeTempFile()
        {
            AudioClipHandler.Configure(new AudioClipPipelineOptions { MaxSourceBytes = 64 });
            var createdBefore = AudioClipHandler.GetTempFileMetrics().Created;

            var response = CreateResponse(BuildWavHeader(channels: 1, bitsPerSample: 16, dataBytes: 64, includeData: true));
            var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(() =>
                response.AsAudioClipAsync(AudioClipType.WAV));

            StringAssert.Contains("MaxSourceBytes", ex.Message);
            Assert.AreEqual(createdBefore, AudioClipHandler.GetTempFileMetrics().Created, "no temp file was reserved");
        }

        [Test]
        public void AsAudioClipAsync_WavDeclaringTooManySamples_RejectedWhenNotStreamed()
        {
            AudioClipHandler.Configure(new AudioClipPipelineOptions
            {
                EnableStreamingForLargeClips = false,
                MaxDecodedSamples = 1000
            });
            var createdBefore = AudioClipHandler.GetTempFileMetrics().Created;

            // 16-bit mono data chunk declaring 2000 samples; the header alone is enough.
            var response = CreateResponse(BuildWavHeader(channels: 1, bitsPerSample: 16, dataBytes: 4000, includeData: false));
            var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(() =>
                response.AsAudioClipAsync(AudioClipType.WAV));

            StringAssert.Contains("MaxDecodedSamples", ex.Message);
            Assert.AreEqual(createdBefore, AudioClipHandler.GetTempFileMetrics().Created, "no temp file was reserved");
        }

        [Test]
        public void AsAudioClipAsync_CompressedWithStreamingDisabled_RejectedWhileBounded()
        {
            AudioClipHandler.Configure(new AudioClipPipelineOptions { EnableStreamingForLargeClips = false });

            var response = CreateResponse(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 });
            var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(() =>
                response.AsAudioClipAsync(AudioClipType.OGG));

            StringAssert.Contains("MaxDecodedSamples", ex.Message);
        }

        [Test]
        public void Plan_SmallCompressedClip_StreamsUnderDefaultBound()
        {
            var body = new ReadOnlySequence<byte>(new byte[1024]);

            var bounded = AudioDecodeBounds.Plan(body, AudioClipType.MP3, new AudioClipPipelineOptions());
            Assert.IsTrue(bounded.Stream, "compressed clips stream regardless of size while bounded");
            Assert.IsFalse(bounded.AllowInMemoryDecode);

            var unbounded = AudioDecodeBounds.Plan(
                body,
                AudioClipType.OGG,
                new AudioClipPipelineOptions { MaxDecodedSamples = null });
            Assert.IsFalse(unbounded.Stream, "the explicit opt-out keeps the size threshold");
            Assert.IsTrue(unbounded.AllowInMemoryDecode);
        }

        [Test]
        public void Plan_SmallWavWithinBound_DecodesInMemory()
        {
            var wav = BuildWavHeader(channels: 2, bitsPerSample: 16, dataBytes: 400, includeData: true);

            var plan = AudioDecodeBounds.Plan(new ReadOnlySequence<byte>(wav), AudioClipType.WAV, new AudioClipPipelineOptions());

            Assert.IsFalse(plan.Stream);
            Assert.IsTrue(plan.AllowInMemoryDecode);
        }

        [Test]
        public void Plan_LargeDeclaredWavThatStreams_SkipsInMemoryDecode()
        {
            var wav = BuildWavHeader(channels: 1, bitsPerSample: 8, dataBytes: 5000, includeData: false);
            var options = new AudioClipPipelineOptions { MaxDecodedSamples = 1000, StreamingThresholdBytes = 1 };

            var plan = AudioDecodeBounds.Plan(new ReadOnlySequence<byte>(wav), AudioClipType.WAV, options);

            Assert.IsTrue(plan.Stream);
            Assert.IsFalse(plan.AllowInMemoryDecode);
        }

        [Test]
        public void TryReadDeclaredPcmSamples_ReadsAiffCommChunk()
        {
            var aiff = new byte[12 + 8 + 18];
            WriteAscii(aiff, 0, "FORM");
            WriteAscii(aiff, 8, "AIFF");
            WriteAscii(aiff, 12, "COMM");
            aiff[19] = 18;     // chunk size
            aiff[21] = 2;      // channels
            aiff[23] = 0x01;   // numSampleFrames = 0x00010000
            aiff[27] = 16;     // sample size

            Assert.IsTrue(AudioDecodeBounds.TryReadDeclaredPcmSamples(
                new ReadOnlySequence<byte>(aiff), AudioClipType.AIFF, out var samples));
            Assert.AreEqual(0x10000L * 2, samples);
        }

        private static byte[] BuildWavHeader(int channels, int bitsPerSample, int dataBytes, bool includeData)
        {
            var bytes = new byte[44 + (includeData ? dataBytes : 0)];
            WriteAscii(bytes, 0, "RIFF");
            WriteInt32LittleEndian(bytes, 4, 36 + dataBytes);
            WriteAscii(bytes, 8, "WAVE");
            WriteAscii(bytes, 12, "fmt ");
            WriteInt32LittleEndian(bytes, 16, 16);
            bytes[20] = 1; // PCM
            bytes[22] = (byte)channels;
            WriteInt32LittleEndian(bytes, 24, 22050);
            var blockAlign = channels * bitsPerSample / 8;
            WriteInt32LittleEndian(bytes, 28, 22050 * blockAlign);
            bytes[32] = (byte)blockAlign;
            bytes[34] = (byte)bitsPerSample;
            WriteAscii(bytes, 36, "data");
            WriteInt32LittleEndian(bytes, 40, dataBytes);
            return bytes;
        }

        private static void WriteAscii(byte[] bytes, int offset, string value)
        {
            for (var i = 0; i < value.Length; i++)
                bytes[offset + i] = (byte)value[i];
        }

        private static void WriteInt32LittleEndian(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static UHttpResponse CreateResponse(byte[] body)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "audio/wav");

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/audio"));
            return new UHttpResponse(HttpStatusCode.OK, headers, body, TimeSpan.Zero, request);
        }
    }
}
