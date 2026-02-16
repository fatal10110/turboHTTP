using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Options for texture decode behavior.
    /// </summary>
    public sealed class TextureOptions
    {
        public bool Readable { get; set; }
        public bool MipMaps { get; set; } = true;
        public bool Linear { get; set; }
        public TextureFormat Format { get; set; } = TextureFormat.RGBA32;

        /// <summary>
        /// When true, Content-Type must start with image/.
        /// Set false for non-compliant APIs.
        /// </summary>
        public bool ValidateImageContentType { get; set; } = true;

        /// <summary>
        /// Optional max payload size guard (in bytes) before decode.
        /// </summary>
        public long? MaxBodyBytes { get; set; }
    }

    /// <summary>
    /// Unity image helpers for <see cref="UHttpResponse"/> and <see cref="UHttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Decode uses synchronous Texture2D.LoadImage on the main thread. Large payloads
    /// can stall frames. Caller owns the returned Texture2D/Sprite and must destroy it.
    /// </remarks>
    public static class Texture2DHandler
    {
        /// <summary>
        /// Converts response bytes into a Texture2D.
        /// </summary>
        public static Texture2D AsTexture2D(this UHttpResponse response, TextureOptions options = null)
        {
            var decodeInput = PrepareDecodeInput(response, options);
            return MainThreadDispatcher.Execute(() => DecodeTexture(decodeInput));
        }

        /// <summary>
        /// Downloads an image and decodes it into Texture2D.
        /// </summary>
        public static async Task<Texture2D> GetTextureAsync(
            this UHttpClient client,
            string url,
            TextureOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            var response = await client
                .Get(url)
                .WithHeader("Accept", "image/*")
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            var decodeInput = PrepareDecodeInput(response, options);
            return await MainThreadDispatcher.ExecuteAsync(
                () => DecodeTexture(decodeInput),
                cancellationToken);
        }

        /// <summary>
        /// Creates a sprite from a texture on the main thread.
        /// </summary>
        public static Sprite AsSprite(this Texture2D texture, Vector2? pivot = null, float pixelsPerUnit = 100f)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (pixelsPerUnit <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pixelsPerUnit),
                    pixelsPerUnit,
                    "pixelsPerUnit must be > 0.");
            }

            return MainThreadDispatcher.Execute(() =>
            {
                var rect = new Rect(0f, 0f, texture.width, texture.height);
                var spritePivot = pivot ?? new Vector2(0.5f, 0.5f);
                return Sprite.Create(texture, rect, spritePivot, pixelsPerUnit);
            });
        }

        /// <summary>
        /// Downloads an image and creates a sprite.
        /// </summary>
        public static async Task<Sprite> GetSpriteAsync(
            this UHttpClient client,
            string url,
            TextureOptions options = null,
            Vector2? pivot = null,
            float pixelsPerUnit = 100f,
            CancellationToken cancellationToken = default)
        {
            var texture = await client.GetTextureAsync(url, options, cancellationToken);
            return texture.AsSprite(pivot, pixelsPerUnit);
        }

        private static DecodeInput PrepareDecodeInput(UHttpResponse response, TextureOptions options)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));

            options ??= new TextureOptions();

            if (response.Body.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Cannot decode Texture2D because response body is empty.");
            }

            ValidateContentType(response, options);
            ValidateBodyLength(response, options);

            // Response body may be sourced from pooled buffers; copy before dispatch.
            return new DecodeInput(response.Body.ToArray(), options);
        }

        private static void ValidateContentType(UHttpResponse response, TextureOptions options)
        {
            if (!options.ValidateImageContentType)
                return;

            var contentType = response.Headers?.Get("Content-Type");
            if (string.IsNullOrWhiteSpace(contentType) ||
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Cannot decode Texture2D because Content-Type is not image/*; " +
                    "set TextureOptions.ValidateImageContentType = false to opt out " +
                    "for non-compliant APIs.");
            }
        }

        private static void ValidateBodyLength(UHttpResponse response, TextureOptions options)
        {
            if (!options.MaxBodyBytes.HasValue)
                return;

            if (options.MaxBodyBytes.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.MaxBodyBytes),
                    options.MaxBodyBytes.Value,
                    "MaxBodyBytes must be greater than 0 when set.");
            }

            if (response.Body.Length > options.MaxBodyBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Image payload is {response.Body.Length} bytes, exceeding configured max " +
                    $"{options.MaxBodyBytes.Value} bytes.");
            }
        }

        private static Texture2D DecodeTexture(DecodeInput decodeInput)
        {
            var texture = new Texture2D(
                width: 2,
                height: 2,
                textureFormat: decodeInput.Options.Format,
                mipChain: decodeInput.Options.MipMaps,
                linear: decodeInput.Options.Linear);

            try
            {
                if (!texture.LoadImage(
                    decodeInput.ImageBytes,
                    markNonReadable: !decodeInput.Options.Readable))
                {
                    throw new InvalidOperationException(
                        "Texture2D.LoadImage returned false. The payload is not a supported image format.");
                }

                return texture;
            }
            catch (InvalidOperationException)
            {
                DestroyUnityObject(texture);
                throw;
            }
            catch (Exception ex)
            {
                DestroyUnityObject(texture);
                throw new InvalidOperationException(
                    "Failed to decode image bytes into Texture2D.",
                    ex);
            }
        }

        private static void DestroyUnityObject(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(unityObject);
            else
                UnityEngine.Object.DestroyImmediate(unityObject);
        }

        private readonly struct DecodeInput
        {
            public DecodeInput(byte[] imageBytes, TextureOptions options)
            {
                ImageBytes = imageBytes ?? throw new ArgumentNullException(nameof(imageBytes));
                Options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public byte[] ImageBytes { get; }
            public TextureOptions Options { get; }
        }
    }
}
