using System;
using System.Buffers;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// How one audio payload may be decoded under <see cref="AudioClipPipelineOptions"/> bounds.
    /// </summary>
    internal readonly struct AudioDecodePlan
    {
        public AudioDecodePlan(bool stream, bool allowInMemoryDecode)
        {
            Stream = stream;
            AllowInMemoryDecode = allowInMemoryDecode;
        }

        /// <summary> Unity loads the clip with <c>streamAudio = true</c>. </summary>
        public bool Stream { get; }

        /// <summary> A fully in-memory decode (managed decoders) stays within the sample bound. </summary>
        public bool AllowInMemoryDecode { get; }
    }

    /// <summary>
    /// Source-size and decoded-size bounds for <see cref="AudioClipHandler"/>, checked before a
    /// decode slot, temp file or decoded buffer is allocated. Stateless.
    /// </summary>
    internal static class AudioDecodeBounds
    {
        internal static AudioDecodePlan Plan(
            ReadOnlySequence<byte> body,
            AudioClipType audioType,
            AudioClipPipelineOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            bool compressed;
            switch (audioType)
            {
                case AudioClipType.WAV:
                case AudioClipType.AIFF:
                    compressed = false;
                    break;
                case AudioClipType.MP3:
                case AudioClipType.OGG:
                    compressed = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(audioType), audioType, "Unsupported audio type.");
            }

            var length = body.Length;
            if (options.MaxSourceBytes.HasValue && length > options.MaxSourceBytes.Value)
            {
                throw new InvalidOperationException(
                    "Audio payload is " + length + " bytes, exceeding configured MaxSourceBytes " +
                    options.MaxSourceBytes.Value + ".");
            }

            var streamBySize =
                options.EnableStreamingForLargeClips &&
                length >= options.StreamingThresholdBytes;

            var maxSamples = options.MaxDecodedSamples;
            if (!maxSamples.HasValue)
                return new AudioDecodePlan(streamBySize, allowInMemoryDecode: true);

            if (compressed)
            {
                // MP3 and OGG headers carry no reliable decoded length, and a crafted stream can
                // expand by orders of magnitude, so only a streamed load is bounded.
                if (!options.EnableStreamingForLargeClips)
                {
                    throw new InvalidOperationException(
                        "Cannot decode " + audioType + " with MaxDecodedSamples set and streaming " +
                        "disabled: compressed clips declare no reliable decoded size and must be " +
                        "streamed. Enable AudioClipPipelineOptions.EnableStreamingForLargeClips or " +
                        "set MaxDecodedSamples = null.");
                }

                return new AudioDecodePlan(stream: true, allowInMemoryDecode: false);
            }

            var withinBound =
                TryReadDeclaredPcmSamples(body, audioType, out var declaredSamples) &&
                declaredSamples <= maxSamples.Value;

            if (!withinBound && !streamBySize)
            {
                throw new InvalidOperationException(
                    "Audio clip exceeds configured MaxDecodedSamples " + maxSamples.Value +
                    " or its " + audioType + " header declares no readable sample count.");
            }

            return new AudioDecodePlan(streamBySize, withinBound);
        }

        /// <summary>
        /// Reads the decoded sample count (sample frames x channels) a WAV or AIFF header declares.
        /// Declared, not clamped to the payload: a decoder may size its buffer from the header.
        /// </summary>
        internal static bool TryReadDeclaredPcmSamples(
            ReadOnlySequence<byte> body,
            AudioClipType audioType,
            out long samples)
        {
            switch (audioType)
            {
                case AudioClipType.WAV:
                    return TryReadWavSamples(body, out samples);
                case AudioClipType.AIFF:
                    return TryReadAiffSamples(body, out samples);
                default:
                    samples = 0;
                    return false;
            }
        }

        private static bool TryReadWavSamples(ReadOnlySequence<byte> body, out long samples)
        {
            samples = 0;
            Span<byte> header = stackalloc byte[16];

            if (!TryCopy(body, 0, header.Slice(0, 12)) ||
                !IsAscii(header, 0, "RIFF") ||
                !IsAscii(header, 8, "WAVE"))
            {
                return false;
            }

            var haveFormat = false;
            int formatTag = 0, channels = 0, bitsPerSample = 0;
            long dataSize = -1;

            long offset = 12;
            while (offset + 8 <= body.Length)
            {
                if (!TryCopy(body, offset, header.Slice(0, 8)))
                    return false;

                long chunkSize = ReadUInt32LittleEndian(header, 4);
                var dataOffset = offset + 8;

                if (IsAscii(header, 0, "fmt "))
                {
                    if (chunkSize < 16 || !TryCopy(body, dataOffset, header))
                        return false;

                    formatTag = header[0] | (header[1] << 8);
                    channels = header[2] | (header[3] << 8);
                    bitsPerSample = header[14] | (header[15] << 8);
                    haveFormat = true;
                }
                else if (IsAscii(header, 0, "data"))
                {
                    dataSize = chunkSize;
                }

                if (haveFormat && dataSize >= 0)
                    break;

                offset = dataOffset + chunkSize + (chunkSize & 1);
            }

            // PCM, IEEE float and WAVE_FORMAT_EXTENSIBLE carry one sample per bytesPerSample;
            // compressed WAV payloads (ADPCM, MP3-in-WAV) do not, so their size is unknown.
            if (!haveFormat || dataSize < 0 || channels <= 0 ||
                (formatTag != 1 && formatTag != 3 && formatTag != 0xFFFE))
            {
                return false;
            }

            var bytesPerSample = (bitsPerSample + 7) / 8;
            if (bytesPerSample <= 0)
                return false;

            samples = dataSize / bytesPerSample;
            return true;
        }

        private static bool TryReadAiffSamples(ReadOnlySequence<byte> body, out long samples)
        {
            samples = 0;
            Span<byte> header = stackalloc byte[12];

            if (!TryCopy(body, 0, header) ||
                !IsAscii(header, 0, "FORM") ||
                !(IsAscii(header, 8, "AIFF") || IsAscii(header, 8, "AIFC")))
            {
                return false;
            }

            long offset = 12;
            while (offset + 8 <= body.Length)
            {
                if (!TryCopy(body, offset, header.Slice(0, 8)))
                    return false;

                long chunkSize = ReadUInt32BigEndian(header, 4);
                var dataOffset = offset + 8;

                if (IsAscii(header, 0, "COMM"))
                {
                    // numChannels(2) numSampleFrames(4); AIFF-C frames count decoded audio too.
                    if (chunkSize < 6 || !TryCopy(body, dataOffset, header.Slice(0, 6)))
                        return false;

                    var channels = (header[0] << 8) | header[1];
                    long frames = ReadUInt32BigEndian(header, 2);
                    if (channels <= 0)
                        return false;

                    samples = frames * channels;
                    return true;
                }

                offset = dataOffset + chunkSize + (chunkSize & 1);
            }

            return false;
        }

        private static bool TryCopy(ReadOnlySequence<byte> body, long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > body.Length - destination.Length)
                return false;

            body.Slice(offset, destination.Length).CopyTo(destination);
            return true;
        }

        private static bool IsAscii(ReadOnlySpan<byte> bytes, int offset, string expected)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (bytes[offset + i] != (byte)expected[i])
                    return false;
            }

            return true;
        }

        private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (uint)(bytes[offset] |
                          (bytes[offset + 1] << 8) |
                          (bytes[offset + 2] << 16) |
                          (bytes[offset + 3] << 24));
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (uint)((bytes[offset] << 24) |
                          (bytes[offset + 1] << 16) |
                          (bytes[offset + 2] << 8) |
                          bytes[offset + 3]);
        }
    }
}
