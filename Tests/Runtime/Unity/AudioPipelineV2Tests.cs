using System;
using NUnit.Framework;
using TurboHTTP.Unity;
using TurboHTTP.Unity.Decoders;

namespace TurboHTTP.Tests.UnityModule
{
    public class AudioPipelineV2Tests
    {
        [Test]
        public void TempFileManager_RespectsMaxActiveFileLimit()
        {
            var manager = UnityTempFileManager.Shared;
            var original = manager.GetOptions();

            manager.Configure(new UnityTempFileManagerOptions
            {
                ShardCount = 4,
                MaxActiveFiles = 2,
                MaxConcurrentIo = 1,
                CleanupRetryCount = 1,
                CleanupRetryDelay = TimeSpan.Zero
            });

            try
            {
                Assert.IsTrue(manager.TryReservePath(".wav", out var p1));
                Assert.IsTrue(manager.TryReservePath(".wav", out var p2));
                Assert.IsFalse(manager.TryReservePath(".wav", out _));

                manager.ReleaseAndScheduleDelete(p1);
                manager.ReleaseAndScheduleDelete(p2);
            }
            finally
            {
                manager.Configure(original);
            }
        }

        [Test]
        public void WavPcmDecoder_DecodeAsync_ParsesPcm16Payload()
        {
            var wav = BuildPcm16Wav(sampleRate: 8000, channels: 1, samples: new short[] { 0, 16384, -16384, 0 });
            var decoder = new WavPcmDecoder();

            var decoded = decoder.DecodeAsync(wav, default).GetAwaiter().GetResult();

            Assert.AreEqual(1, decoded.Channels);
            Assert.AreEqual(8000, decoded.SampleRate);
            Assert.AreEqual(4, decoded.Samples.Length);
        }

        private static byte[] BuildPcm16Wav(int sampleRate, short channels, short[] samples)
        {
            var bytesPerSample = 2;
            var dataSize = samples.Length * bytesPerSample;
            var riffSize = 36 + dataSize;
            var blockAlign = (short)(channels * bytesPerSample);
            var byteRate = sampleRate * blockAlign;

            var buffer = new byte[44 + dataSize];
            WriteAscii(buffer, 0, "RIFF");
            WriteInt32Le(buffer, 4, riffSize);
            WriteAscii(buffer, 8, "WAVE");
            WriteAscii(buffer, 12, "fmt ");
            WriteInt32Le(buffer, 16, 16);
            WriteInt16Le(buffer, 20, 1);
            WriteInt16Le(buffer, 22, channels);
            WriteInt32Le(buffer, 24, sampleRate);
            WriteInt32Le(buffer, 28, byteRate);
            WriteInt16Le(buffer, 32, blockAlign);
            WriteInt16Le(buffer, 34, 16);
            WriteAscii(buffer, 36, "data");
            WriteInt32Le(buffer, 40, dataSize);

            var offset = 44;
            for (var i = 0; i < samples.Length; i++)
            {
                WriteInt16Le(buffer, offset, samples[i]);
                offset += 2;
            }

            return buffer;
        }

        private static void WriteAscii(byte[] buffer, int offset, string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                buffer[offset + i] = (byte)value[i];
            }
        }

        private static void WriteInt16Le(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt32Le(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
