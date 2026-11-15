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
        public void Plan_SmallCompressedClip_LoadsCompressedUnderDefaultBound()
        {
            var body = new ReadOnlySequence<byte>(new byte[1024]);

            // Default options: a small MP3 must not stream, because every streamed clip holds a
            // temp-file slot until it is destroyed and MaxActiveFiles (128) would then fail
            // every later audio load. It loads compressed in memory and releases its file.
            var bounded = AudioDecodeBounds.Plan(body, AudioClipType.MP3, new AudioClipPipelineOptions());
            Assert.IsFalse(bounded.Stream, "small compressed clips do not hold a temp file");
            Assert.IsTrue(bounded.KeepCompressed, "never decoded to PCM up front while bounded");
            Assert.IsFalse(bounded.AllowInMemoryDecode);

            var noStreaming = AudioDecodeBounds.Plan(
                body,
                AudioClipType.OGG,
                new AudioClipPipelineOptions { EnableStreamingForLargeClips = false });
            Assert.IsFalse(noStreaming.Stream);
            Assert.IsTrue(noStreaming.KeepCompressed, "streaming disabled still loads, compressed");

            var unbounded = AudioDecodeBounds.Plan(
                body,
                AudioClipType.OGG,
                new AudioClipPipelineOptions { MaxDecodedSamples = null });
            Assert.IsFalse(unbounded.Stream, "the explicit opt-out keeps the size threshold");
            Assert.IsFalse(unbounded.KeepCompressed);
            Assert.IsTrue(unbounded.AllowInMemoryDecode);
        }

        [Test]
        public void Plan_CompressedClipAtStreamingThreshold_StillStreams()
        {
            var body = new ReadOnlySequence<byte>(new byte[64]);
            var options = new AudioClipPipelineOptions { StreamingThresholdBytes = 64 };

            var plan = AudioDecodeBounds.Plan(body, AudioClipType.MP3, options);

            Assert.IsTrue(plan.Stream);
            Assert.IsFalse(plan.KeepCompressed);
            Assert.IsFalse(plan.AllowInMemoryDecode);
        }

        [Test]
        public void TryReadDeclaredPcmSamples_WavPastChunkCap_FailsClosedOnMultiSegmentBody()
        {
            // fmt and data come after more empty chunks than the walk inspects, spread over
            // 16-byte segments so every chunk lookup seeks across segments.
            var body = BuildSegmented(BuildWavAfterJunk(AudioDecodeBounds.MaxChunksScanned + 1), segmentSize: 16);
            Assert.IsFalse(body.IsSingleSegment);
            Assert.IsFalse(AudioDecodeBounds.TryReadDeclaredPcmSamples(body, AudioClipType.WAV, out _));

            // The same layout with fewer empty chunks than the cap still reads.
            Assert.IsTrue(AudioDecodeBounds.TryReadDeclaredPcmSamples(
                BuildSegmented(BuildWavAfterJunk(1), segmentSize: 16), AudioClipType.WAV, out var samples));
            Assert.AreEqual(4, samples);
        }

        [Test]
        public void TryReadDeclaredPcmSamples_AiffPastChunkCap_FailsClosed()
        {
            var junkChunks = AudioDecodeBounds.MaxChunksScanned + 1;
            var aiff = new byte[12 + junkChunks * 8 + 8 + 18];
            WriteAscii(aiff, 0, "FORM");
            WriteAscii(aiff, 8, "AIFF");
            for (var i = 0; i < junkChunks; i++)
                WriteAscii(aiff, 12 + i * 8, "JUNK");
            var comm = 12 + junkChunks * 8;
            WriteAscii(aiff, comm, "COMM");
            aiff[comm + 7] = 18;   // chunk size
            aiff[comm + 9] = 1;    // channels
            aiff[comm + 13] = 1;   // numSampleFrames

            Assert.IsFalse(AudioDecodeBounds.TryReadDeclaredPcmSamples(
                BuildSegmented(aiff, segmentSize: 16), AudioClipType.AIFF, out _));
        }

        [Test]
        public void Plan_WavPastChunkCap_RejectedWhenNotStreamed()
        {
            var body = BuildSegmented(BuildWavAfterJunk(AudioDecodeBounds.MaxChunksScanned + 1), segmentSize: 16);

            Assert.Throws<InvalidOperationException>(() => AudioDecodeBounds.Plan(
                body,
                AudioClipType.WAV,
                new AudioClipPipelineOptions { EnableStreamingForLargeClips = false }));
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

        // A 16-bit mono WAV with 8 data bytes whose fmt and data chunks follow empty JUNK chunks.
        private static byte[] BuildWavAfterJunk(int junkChunks)
        {
            var wav = BuildWavHeader(channels: 1, bitsPerSample: 16, dataBytes: 8, includeData: true);
            var bytes = new byte[wav.Length + junkChunks * 8];
            Array.Copy(wav, 0, bytes, 0, 12);
            for (var i = 0; i < junkChunks; i++)
                WriteAscii(bytes, 12 + i * 8, "JUNK");
            Array.Copy(wav, 12, bytes, 12 + junkChunks * 8, wav.Length - 12);
            return bytes;
        }

        private static ReadOnlySequence<byte> BuildSegmented(byte[] bytes, int segmentSize)
        {
            TestSegment first = null, last = null;
            for (var offset = 0; offset < bytes.Length; offset += segmentSize)
            {
                var memory = new ReadOnlyMemory<byte>(bytes, offset, Math.Min(segmentSize, bytes.Length - offset));
                var segment = new TestSegment(memory, offset);
                if (first == null)
                    first = segment;
                else
                    last.SetNext(segment);
                last = segment;
            }

            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        private sealed class TestSegment : ReadOnlySequenceSegment<byte>
        {
            public TestSegment(ReadOnlyMemory<byte> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public void SetNext(TestSegment next)
            {
                Next = next;
            }
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
