using System;

namespace TurboHTTP.Unity.Decoders
{
    /// <summary>
    /// Decoded RGBA image payload.
    /// </summary>
    public sealed class DecodedImage
    {
        public DecodedImage(int width, int height, byte[] rgba32)
        {
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
            Rgba32 = rgba32 ?? throw new ArgumentNullException(nameof(rgba32));

            long requiredBytesLong = (long)width * height * 4;
            if (requiredBytesLong > int.MaxValue)
            {
                throw new ArgumentException(
                    "Image dimensions are too large (width * height * 4 exceeds int.MaxValue).",
                    nameof(rgba32));
            }

            var requiredBytes = (int)requiredBytesLong;
            if (rgba32.Length != requiredBytes)
            {
                throw new ArgumentException(
                    "RGBA payload length does not match width*height*4.",
                    nameof(rgba32));
            }

            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Rgba32 { get; }
    }
}
