using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Managed WAV PCM/float decoder.
    /// </summary>
    public sealed class WavPcmDecoder : IAudioDecoder
    {
        public string Id => "wav-pcm";

        public bool CanDecode(string contentType, string fileExtension)
        {
            if (!string.IsNullOrWhiteSpace(contentType) &&
                (contentType.IndexOf("audio/wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("audio/x-wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("audio/wave", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(fileExtension, ".wav", StringComparison.OrdinalIgnoreCase);
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DecodedAudio> DecodeAsync(ReadOnlyMemory<byte> encodedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(ParseWav(encodedBytes.Span, cancellationToken));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "WAV decode failed at parse stage.",
                    ex);
            }
        }

        private static DecodedAudio ParseWav(ReadOnlySpan<byte> wav, CancellationToken cancellationToken)
        {
            if (wav.Length < 44)
                throw new InvalidOperationException("WAV payload is too small.");

            if (!MatchAscii(wav, 0, "RIFF") || !MatchAscii(wav, 8, "WAVE"))
                throw new InvalidOperationException("WAV header is invalid (RIFF/WAVE missing).");

            int fmtOffset = -1;
            int fmtSize = 0;
            int dataOffset = -1;
            int dataSize = 0;

            var offset = 12;
            while (offset + 8 <= wav.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkId = ReadAscii(wav, offset, 4);
                var chunkSize = ReadInt32LittleEndian(wav, offset + 4);
                if (chunkSize < 0)
                    throw new InvalidOperationException("WAV chunk size is invalid.");

                var chunkDataOffset = offset + 8;
                if (chunkDataOffset + chunkSize > wav.Length)
                    throw new InvalidOperationException("WAV chunk extends beyond payload length.");

                if (chunkId == "fmt ")
                {
                    fmtOffset = chunkDataOffset;
                    fmtSize = chunkSize;
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataOffset;
                    dataSize = chunkSize;
                }

                offset = chunkDataOffset + chunkSize;
                if ((offset & 1) == 1)
                    offset++;
            }

            if (fmtOffset < 0 || dataOffset < 0)
                throw new InvalidOperationException("WAV payload is missing fmt or data chunk.");

            if (fmtSize < 16)
                throw new InvalidOperationException("WAV fmt chunk is too small.");

            var audioFormat = ReadInt16LittleEndian(wav, fmtOffset + 0);
            var channels = ReadInt16LittleEndian(wav, fmtOffset + 2);
            var sampleRate = ReadInt32LittleEndian(wav, fmtOffset + 4);
            var bitsPerSample = ReadInt16LittleEndian(wav, fmtOffset + 14);

            if (channels <= 0)
                throw new InvalidOperationException("WAV channels must be > 0.");
            if (sampleRate <= 0)
                throw new InvalidOperationException("WAV sampleRate must be > 0.");
            if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
                throw new InvalidOperationException("WAV bitsPerSample is unsupported.");
            if (audioFormat != 1 && audioFormat != 3)
                throw new InvalidOperationException("WAV audio format is unsupported (only PCM/IEEE float). ");

            var bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0)
                throw new InvalidOperationException("WAV bytesPerSample is invalid.");

            var frameSize = bytesPerSample * channels;
            if (frameSize <= 0)
                throw new InvalidOperationException("WAV frame size is invalid.");

            var frameCount = dataSize / frameSize;
            var totalSamples = frameCount * channels;
            var samples = new float[totalSamples];

            var sampleOffset = dataOffset;
            for (var i = 0; i < totalSamples; i++)
            {
                if ((i & 0x1FFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                samples[i] = audioFormat == 3
                    ? ReadFloatSample(wav, sampleOffset, bitsPerSample)
                    : ReadPcmSample(wav, sampleOffset, bitsPerSample);

                sampleOffset += bytesPerSample;
            }

            return new DecodedAudio(channels, sampleRate, samples);
        }

        private static float ReadPcmSample(ReadOnlySpan<byte> wav, int offset, int bitsPerSample)
        {
            switch (bitsPerSample)
            {
                case 8:
                    // Unsigned 8-bit PCM.
                    return (wav[offset] - 128f) / 128f;

                case 16:
                    return ReadInt16LittleEndian(wav, offset) / 32768f;

                case 24:
                {
                    var value = wav[offset] |
                                (wav[offset + 1] << 8) |
                                (wav[offset + 2] << 16);

                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xFF000000);

                    return value / 8388608f;
                }

                case 32:
                    return ReadInt32LittleEndian(wav, offset) / 2147483648f;

                default:
                    throw new InvalidOperationException("Unsupported PCM bit depth.");
            }
        }

        private static float ReadFloatSample(ReadOnlySpan<byte> wav, int offset, int bitsPerSample)
        {
            if (bitsPerSample != 32)
            {
                throw new InvalidOperationException(
                    "IEEE float WAV currently supports only 32-bit samples.");
            }

            if (BitConverter.IsLittleEndian)
            {
                return MemoryMarshal.Read<float>(wav.Slice(offset, 4));
            }

            Span<byte> reversed = stackalloc byte[4];
            reversed[0] = wav[offset + 3];
            reversed[1] = wav[offset + 2];
            reversed[2] = wav[offset + 1];
            reversed[3] = wav[offset + 0];
            return MemoryMarshal.Read<float>(reversed);
        }

        private static bool MatchAscii(ReadOnlySpan<byte> bytes, int offset, string expected)
        {
            if (offset < 0 || offset + expected.Length > bytes.Length)
                return false;

            for (var i = 0; i < expected.Length; i++)
            {
                if (bytes[offset + i] != expected[i])
                    return false;
            }

            return true;
        }

        private static string ReadAscii(ReadOnlySpan<byte> bytes, int offset, int count)
        {
            return Encoding.ASCII.GetString(bytes.Slice(offset, count).ToArray());
        }

        private static short ReadInt16LittleEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (short)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static int ReadInt32LittleEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }
    }
}
