using System;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Decoded PCM audio payload.
    /// </summary>
    public sealed class DecodedAudio
    {
        public DecodedAudio(int channels, int sampleRate, float[] samples)
        {
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));

            Channels = channels;
            SampleRate = sampleRate;
        }

        public int Channels { get; }
        public int SampleRate { get; }
        public float[] Samples { get; }
        public int SampleFrames => Channels == 0 ? 0 : Samples.Length / Channels;
    }
}
