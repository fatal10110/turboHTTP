using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Managed AIFF PCM decoder.
    /// </summary>
    public sealed class AiffPcmDecoder : IAudioDecoder
    {
        public string Id => "aiff-pcm";

        public bool CanDecode(string contentType, string fileExtension)
        {
            if (!string.IsNullOrWhiteSpace(contentType) &&
                (contentType.IndexOf("audio/aiff", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("audio/x-aiff", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return string.Equals(fileExtension, ".aif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileExtension, ".aiff", StringComparison.OrdinalIgnoreCase);
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
                return Task.FromResult(ParseAiff(encodedBytes.Span, cancellationToken));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "AIFF decode failed at parse stage.",
                    ex);
            }
        }

        private static DecodedAudio ParseAiff(ReadOnlySpan<byte> aiff, CancellationToken cancellationToken)
        {
            if (aiff.Length < 54)
                throw new InvalidOperationException("AIFF payload is too small.");

            if (!MatchAscii(aiff, 0, "FORM"))
                throw new InvalidOperationException("AIFF header is invalid (FORM missing).");

            var formType = ReadAscii(aiff, 8, 4);
            if (!string.Equals(formType, "AIFF", StringComparison.Ordinal) &&
                !string.Equals(formType, "AIFC", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AIFF FORM type is unsupported.");
            }

            int channels = 0;
            int sampleRate = 0;
            int sampleSize = 0;
            int sampleFrames = 0;

            int soundDataOffset = -1;
            int soundDataLength = 0;

            var offset = 12;
            while (offset + 8 <= aiff.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkId = ReadAscii(aiff, offset, 4);
                var chunkSize = ReadInt32BigEndian(aiff, offset + 4);
                if (chunkSize < 0)
                    throw new InvalidOperationException("AIFF chunk size is invalid.");

                var chunkDataOffset = offset + 8;
                if (chunkDataOffset + chunkSize > aiff.Length)
                    throw new InvalidOperationException("AIFF chunk extends beyond payload length.");

                if (chunkId == "COMM")
                {
                    if (chunkSize < 18)
                        throw new InvalidOperationException("AIFF COMM chunk is too small.");

                    channels = ReadInt16BigEndian(aiff, chunkDataOffset + 0);
                    sampleFrames = ReadInt32BigEndian(aiff, chunkDataOffset + 2);
                    sampleSize = ReadInt16BigEndian(aiff, chunkDataOffset + 6);
                    sampleRate = (int)Math.Round(ReadExtended80(aiff.Slice(chunkDataOffset + 8, 10)));

                    if (channels <= 0 || sampleFrames < 0 || sampleSize <= 0 || sampleRate <= 0)
                    {
                        throw new InvalidOperationException("AIFF COMM values are invalid.");
                    }
                }
                else if (chunkId == "SSND")
                {
                    if (chunkSize < 8)
                        throw new InvalidOperationException("AIFF SSND chunk is too small.");

                    var dataOffset = ReadInt32BigEndian(aiff, chunkDataOffset + 0);
                    var blockSize = ReadInt32BigEndian(aiff, chunkDataOffset + 4);
                    if (dataOffset < 0 || blockSize < 0)
                        throw new InvalidOperationException("AIFF SSND offset/block size is invalid.");

                    soundDataOffset = chunkDataOffset + 8 + dataOffset;
                    soundDataLength = chunkSize - 8 - dataOffset;

                    if (soundDataOffset < 0 || soundDataOffset > aiff.Length || soundDataLength < 0)
                        throw new InvalidOperationException("AIFF SSND payload is invalid.");

                    if (soundDataOffset + soundDataLength > aiff.Length)
                        throw new InvalidOperationException("AIFF SSND data extends beyond payload length.");
                }

                offset = chunkDataOffset + chunkSize;
                if ((offset & 1) == 1)
                    offset++;
            }

            if (channels <= 0 || sampleRate <= 0 || sampleSize <= 0)
                throw new InvalidOperationException("AIFF COMM chunk was not found.");
            if (soundDataOffset < 0)
                throw new InvalidOperationException("AIFF SSND chunk was not found.");
            if (sampleSize != 8 && sampleSize != 16 && sampleSize != 24 && sampleSize != 32)
                throw new InvalidOperationException("AIFF sample size is unsupported.");

            var bytesPerSample = sampleSize / 8;
            var frameSize = bytesPerSample * channels;
            if (frameSize <= 0)
                throw new InvalidOperationException("AIFF frame size is invalid.");

            var availableFrameCount = soundDataLength / frameSize;
            if (sampleFrames > 0)
                availableFrameCount = Math.Min(availableFrameCount, sampleFrames);

            var totalSamples = availableFrameCount * channels;
            var samples = new float[totalSamples];

            var sampleOffset = soundDataOffset;
            for (var i = 0; i < totalSamples; i++)
            {
                if ((i & 0x1FFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                samples[i] = ReadPcmSampleBigEndian(aiff, sampleOffset, sampleSize);
                sampleOffset += bytesPerSample;
            }

            return new DecodedAudio(channels, sampleRate, samples);
        }

        private static float ReadPcmSampleBigEndian(ReadOnlySpan<byte> bytes, int offset, int bits)
        {
            switch (bits)
            {
                case 8:
                    return sbyteToFloat(unchecked((sbyte)bytes[offset]));

                case 16:
                    return ReadInt16BigEndian(bytes, offset) / 32768f;

                case 24:
                {
                    var value = (bytes[offset] << 16)
                              | (bytes[offset + 1] << 8)
                              | bytes[offset + 2];

                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xFF000000);

                    return value / 8388608f;
                }

                case 32:
                    return ReadInt32BigEndian(bytes, offset) / 2147483648f;

                default:
                    throw new InvalidOperationException("Unsupported AIFF sample size.");
            }
        }

        private static float sbyteToFloat(sbyte value)
        {
            return Math.Max(-1f, value / 128f);
        }

        private static double ReadExtended80(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 10)
                throw new ArgumentException("Extended80 value requires 10 bytes.", nameof(bytes));

            var exponent = ReadInt16BigEndian(bytes, 0);
            ulong mantissa =
                ((ulong)bytes[2] << 56) |
                ((ulong)bytes[3] << 48) |
                ((ulong)bytes[4] << 40) |
                ((ulong)bytes[5] << 32) |
                ((ulong)bytes[6] << 24) |
                ((ulong)bytes[7] << 16) |
                ((ulong)bytes[8] << 8) |
                bytes[9];

            if (exponent == 0 && mantissa == 0)
                return 0d;

            var sign = (exponent & 0x8000) != 0 ? -1d : 1d;
            exponent &= 0x7FFF;

            if (exponent == 0x7FFF)
                return sign * double.PositiveInfinity;

            var fraction = mantissa / Math.Pow(2d, 63d);
            return sign * fraction * Math.Pow(2d, exponent - 16383d);
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

        private static short ReadInt16BigEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (short)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (bytes[offset] << 24)
                | (bytes[offset + 1] << 16)
                | (bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }
    }
}
