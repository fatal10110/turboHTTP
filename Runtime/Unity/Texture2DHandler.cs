using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Unity.Decoders;
using UnityEngine;
using Debug = UnityEngine.Debug;

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

        /// <summary>
        /// Initial Texture2D construction format. Unity's Texture2D.LoadImage may replace
        /// the underlying texture data format during decode.
        /// </summary>
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

        /// <summary>
        /// Optional max source size guard (in bytes) before decode.
        /// Alias of <see cref="MaxBodyBytes"/> with Phase 15 naming.
        /// </summary>
        public long? MaxSourceBytes { get; set; }

        /// <summary>
        /// Optional max pixel count guard (width * height).
        /// </summary>
        public long? MaxPixels { get; set; }

        /// <summary>
        /// Legacy: max concurrent decode workers for scheduler configuration.
        /// Prefer configuring globally via <see cref="TextureDecodeScheduler.Configure(TextureDecodeSchedulerOptions)"/>.
        /// </summary>
        public int MaxConcurrentDecodes { get; set; } =
            Texture2DHandler.DefaultSchedulerMaxConcurrentDecodes;

        /// <summary>
        /// Legacy: max queued decode requests for scheduler configuration.
        /// Prefer configuring globally via <see cref="TextureDecodeScheduler.Configure(TextureDecodeSchedulerOptions)"/>.
        /// </summary>
        public int MaxQueuedDecodes { get; set; } =
            Texture2DHandler.DefaultSchedulerMaxQueuedDecodes;

        /// <summary>
        /// Enables optional managed threaded decode via decoder registry.
        /// </summary>
        public bool EnableThreadedDecode { get; set; }

        /// <summary>
        /// Minimum payload size (bytes) before threaded decode is considered.
        /// </summary>
        public int ThreadedDecodeMinBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Minimum pixel count before threaded decode is considered.
        /// </summary>
        public int ThreadedDecodeMinPixels { get; set; } = 1024 * 1024;

        /// <summary>
        /// Optional warmup for managed image decoder providers.
        /// </summary>
        public bool WarmupManagedDecoders { get; set; }

        internal long? EffectiveMaxSourceBytes => MaxSourceBytes ?? MaxBodyBytes;
    }

    /// <summary>
    /// Unity image helpers for <see cref="UHttpResponse"/> and <see cref="UHttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Decode uses synchronous Texture2D.LoadImage on the main thread by default.
    /// The async path uses a bounded scheduler and optional managed threaded decode fallback.
    /// Caller owns returned Texture2D/Sprite and must destroy it.
    /// </remarks>
    public static class Texture2DHandler
    {
        internal const int DefaultSchedulerMaxConcurrentDecodes = 2;
        internal const int DefaultSchedulerMaxQueuedDecodes = 128;

        private static int _legacySchedulerOptionWarningIssued;

        /// <summary>
        /// Converts response bytes into a Texture2D.
        /// </summary>
        /// <remarks>
        /// This synchronous API is main-thread only and blocks until work completes.
        /// Prefer <see cref="GetTextureAsync(UHttpClient, string, TextureOptions, CancellationToken)"/>
        /// when calling from worker-thread contexts.
        /// </remarks>
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
                .SendAsync(cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            var decodeInput = PrepareDecodeInput(response, options);
            if (decodeInput.Options.WarmupManagedDecoders)
            {
                await TextureDecodeScheduler.Shared
                    .WarmupAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var totalStart = Stopwatch.GetTimestamp();

            var scheduled = await TextureDecodeScheduler.Shared
                .ScheduleAsync(
                    token => DecodeScheduledAsync(response, decodeInput, token),
                    cancellationToken)
                .ConfigureAwait(false);

            var totalElapsedTicks = Stopwatch.GetTimestamp() - totalStart;
            var totalElapsed = TimeSpan.FromSeconds(totalElapsedTicks / (double)Stopwatch.Frequency);
            var decodeOnly = totalElapsed - scheduled.QueueLatency;
            if (decodeOnly < TimeSpan.Zero)
                decodeOnly = TimeSpan.Zero;

            RecordDecodeDiagnostics(
                response,
                scheduled.QueueLatency,
                decodeOnly,
                decodeInput.LastDecodeUsedThreaded,
                decodeInput.LastDecodeFellBackToUnity);

            return scheduled.Texture;
        }

        /// <summary>
        /// Creates a sprite from a texture on the main thread.
        /// </summary>
        /// <remarks>
        /// This synchronous API is main-thread only and blocks until work completes.
        /// Prefer <see cref="GetSpriteAsync(UHttpClient, string, TextureOptions, Vector2?, float, CancellationToken)"/>
        /// when calling from worker-thread contexts.
        /// </remarks>
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

            return MainThreadDispatcher.Execute(() => CreateSprite(texture, pivot, pixelsPerUnit));
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
            var texture = await client
                .GetTextureAsync(url, options, cancellationToken)
                .ConfigureAwait(false);

            return await MainThreadDispatcher.ExecuteAsync(
                () => CreateSprite(texture, pivot, pixelsPerUnit),
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Texture2D> DecodeScheduledAsync(
            UHttpResponse response,
            DecodeInput decodeInput,
            CancellationToken cancellationToken)
        {
            if (ShouldUseThreadedDecode(decodeInput))
            {
                var threadedTexture = await TryThreadedDecodeAsync(
                    response,
                    decodeInput,
                    cancellationToken).ConfigureAwait(false);

                if (threadedTexture != null)
                {
                    decodeInput.LastDecodeUsedThreaded = true;
                    decodeInput.LastDecodeFellBackToUnity = false;
                    return threadedTexture;
                }

                decodeInput.LastDecodeUsedThreaded = false;
                decodeInput.LastDecodeFellBackToUnity = true;
            }

            decodeInput.LastDecodeUsedThreaded = false;
            return await MainThreadDispatcher.ExecuteAsync(
                () => DecodeTexture(decodeInput),
                cancellationToken).ConfigureAwait(false);
        }

        private static bool ShouldUseThreadedDecode(DecodeInput decodeInput)
        {
            var options = decodeInput.Options;
            if (!options.EnableThreadedDecode)
                return false;

            if (decodeInput.ImageBytes.Length < options.ThreadedDecodeMinBytes)
                return false;

            if (options.ThreadedDecodeMinPixels <= 0)
                return true;

            if (TryReadImageDimensions(decodeInput.ImageBytes, out var width, out var height))
            {
                var pixels = (long)width * height;
                return pixels >= options.ThreadedDecodeMinPixels;
            }

            return true;
        }

        private static async Task<Texture2D> TryThreadedDecodeAsync(
            UHttpResponse response,
            DecodeInput decodeInput,
            CancellationToken cancellationToken)
        {
            var contentType = response.Headers?.Get("Content-Type");
            var sourcePath = response.Request?.Uri?.AbsolutePath ?? string.Empty;

            if (!DecoderRegistry.TryResolveImageDecoder(
                    contentType,
                    sourcePath,
                    out var decoder,
                    out var reason))
            {
                Debug.LogWarning(
                    "[TurboHTTP] Threaded image decode unavailable (" + reason + "). " +
                    "Falling back to Unity Texture2D.LoadImage.");
                return null;
            }

            try
            {
                var decoded = await decoder.DecodeAsync(
                        decodeInput.ImageBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (decodeInput.Options.MaxPixels.HasValue)
                {
                    var pixelCount = (long)decoded.Width * decoded.Height;
                    if (pixelCount > decodeInput.Options.MaxPixels.Value)
                    {
                        throw new InvalidOperationException(
                            "Image exceeds configured MaxPixels limit after managed decode.");
                    }
                }

                return await MainThreadDispatcher.ExecuteAsync(
                    () => CreateTextureFromDecodedImage(decoded, decodeInput.Options),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TurboHTTP] Managed image decode failed and will fall back to Unity decode. " +
                    "ContentType='" +
                    contentType +
                    "', bytes=" +
                    decodeInput.ImageBytes.Length +
                    ". Error: " +
                    ex.Message);

                return null;
            }
        }

        private static Texture2D CreateTextureFromDecodedImage(DecodedImage decoded, TextureOptions options)
        {
            if (decoded == null) throw new ArgumentNullException(nameof(decoded));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var texture = new Texture2D(
                width: decoded.Width,
                height: decoded.Height,
                textureFormat: TextureFormat.RGBA32,
                mipChain: options.MipMaps,
                linear: options.Linear);

            try
            {
                texture.LoadRawTextureData(decoded.Rgba32);
                texture.Apply(
                    updateMipmaps: options.MipMaps,
                    makeNoLongerReadable: !options.Readable);
                return texture;
            }
            catch
            {
                DestroyUnityObject(texture);
                throw;
            }
        }

        private static DecodeInput PrepareDecodeInput(UHttpResponse response, TextureOptions options)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            response.EnsureSuccessStatusCode();

            options ??= new TextureOptions();
            ApplyLegacySchedulerOptions(options);

            if (response.Body.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Cannot decode Texture2D because response body is empty.");
            }

            ValidateContentType(response, options);
            ValidateBodyLength(response, options);
            ValidatePixelGuardFromHeader(response.Body.Span, options);

            return new DecodeInput(GetDecodeBytes(response.Body), options);
        }

        private static void ApplyLegacySchedulerOptions(TextureOptions options)
        {
            if (options.MaxConcurrentDecodes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.MaxConcurrentDecodes),
                    options.MaxConcurrentDecodes,
                    "MaxConcurrentDecodes must be greater than 0.");
            }

            if (options.MaxQueuedDecodes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.MaxQueuedDecodes),
                    options.MaxQueuedDecodes,
                    "MaxQueuedDecodes must be greater than 0.");
            }

            if (options.MaxConcurrentDecodes == DefaultSchedulerMaxConcurrentDecodes &&
                options.MaxQueuedDecodes == DefaultSchedulerMaxQueuedDecodes)
            {
                return;
            }

            var scheduler = TextureDecodeScheduler.Shared;
            var current = scheduler.GetOptions();
            var alreadyConfigured =
                current.MaxConcurrentDecodes != DefaultSchedulerMaxConcurrentDecodes ||
                current.MaxQueuedDecodes != DefaultSchedulerMaxQueuedDecodes;

            if (!alreadyConfigured)
            {
                scheduler.Configure(new TextureDecodeSchedulerOptions
                {
                    MaxConcurrentDecodes = options.MaxConcurrentDecodes,
                    MaxQueuedDecodes = options.MaxQueuedDecodes
                });
            }

            if (Interlocked.CompareExchange(ref _legacySchedulerOptionWarningIssued, 1, 0) == 0)
            {
                Debug.LogWarning(
                    "[TurboHTTP] TextureOptions.MaxConcurrentDecodes and MaxQueuedDecodes are legacy " +
                    "scheduler controls. They now seed global scheduler settings only when defaults " +
                    "are still active. Prefer TextureDecodeScheduler.Shared.Configure(...) at startup.");
            }
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
            var maxSource = options.EffectiveMaxSourceBytes;
            if (!maxSource.HasValue)
                return;

            if (maxSource.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.MaxSourceBytes),
                    maxSource.Value,
                    "MaxSourceBytes must be greater than 0 when set.");
            }

            var contentLengthHeader = response.Headers?.Get("Content-Length");
            if (!string.IsNullOrWhiteSpace(contentLengthHeader) &&
                long.TryParse(contentLengthHeader, out var contentLength) &&
                contentLength > maxSource.Value)
            {
                throw new InvalidOperationException(
                    "Image Content-Length exceeds configured MaxSourceBytes limit.");
            }

            if (response.Body.Length > maxSource.Value)
            {
                throw new InvalidOperationException(
                    $"Image payload is {response.Body.Length} bytes, exceeding configured max " +
                    $"{maxSource.Value} bytes.");
            }
        }

        private static void ValidatePixelGuardFromHeader(ReadOnlySpan<byte> imageBytes, TextureOptions options)
        {
            if (!options.MaxPixels.HasValue)
                return;

            if (!TryReadImageDimensions(imageBytes, out var width, out var height))
                return;

            var pixels = (long)width * height;
            if (pixels > options.MaxPixels.Value)
            {
                throw new InvalidOperationException(
                    "Image dimensions exceed configured MaxPixels limit.");
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

                if (decodeInput.Options.MaxPixels.HasValue)
                {
                    var pixelCount = (long)texture.width * texture.height;
                    if (pixelCount > decodeInput.Options.MaxPixels.Value)
                    {
                        throw new InvalidOperationException(
                            "Image exceeds configured MaxPixels limit.");
                    }
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

        private static byte[] GetDecodeBytes(ReadOnlyMemory<byte> body)
        {
            if (TryGetExactArray(body, out var existing))
                return existing;

            return body.ToArray();
        }

        private static bool TryGetExactArray(ReadOnlyMemory<byte> memory, out byte[] array)
        {
            if (MemoryMarshal.TryGetArray(memory, out var segment) &&
                segment.Array != null &&
                segment.Offset == 0 &&
                segment.Count == segment.Array.Length)
            {
                array = segment.Array;
                return true;
            }

            array = null;
            return false;
        }

        private static bool TryReadImageDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (TryReadPngDimensions(bytes, out width, out height))
                return true;

            if (TryReadJpegDimensions(bytes, out width, out height))
                return true;

            return false;
        }

        private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (bytes.Length < 24)
                return false;

            if (!(bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47))
                return false;

            width = ReadInt32BigEndian(bytes, 16);
            height = ReadInt32BigEndian(bytes, 20);
            return width > 0 && height > 0;
        }

        private static bool TryReadJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                return false;

            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                if (marker == 0xD9 || marker == 0xDA)
                    break;

                if (index + 4 > bytes.Length)
                    break;

                var segmentLength = (bytes[index + 2] << 8) | bytes[index + 3];
                if (segmentLength < 2 || index + 2 + segmentLength > bytes.Length)
                    break;

                var isStartOfFrame =
                    marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3 ||
                    marker == 0xC5 || marker == 0xC6 || marker == 0xC7 ||
                    marker == 0xC9 || marker == 0xCA || marker == 0xCB ||
                    marker == 0xCD || marker == 0xCE || marker == 0xCF;

                if (isStartOfFrame)
                {
                    height = (bytes[index + 5] << 8) | bytes[index + 6];
                    width = (bytes[index + 7] << 8) | bytes[index + 8];
                    return width > 0 && height > 0;
                }

                index += 2 + segmentLength;
            }

            return false;
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (bytes[offset] << 24)
                | (bytes[offset + 1] << 16)
                | (bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        private static void RecordDecodeDiagnostics(
            UHttpResponse response,
            TimeSpan queueLatency,
            TimeSpan decodeDuration,
            bool usedThreadedDecode,
            bool usedFallback)
        {
            if (!(response?.Request?.Metadata is IDictionary<string, object> mutableMetadata))
                return;

            mutableMetadata["unity.texture.queue_latency_ms"] = queueLatency.TotalMilliseconds;
            mutableMetadata["unity.texture.decode_duration_ms"] = decodeDuration.TotalMilliseconds;
            mutableMetadata["unity.texture.threaded_decode"] = usedThreadedDecode;
            mutableMetadata["unity.texture.fallback_decode"] = usedFallback;
        }

        private static Sprite CreateSprite(Texture2D texture, Vector2? pivot, float pixelsPerUnit)
        {
            var rect = new Rect(0f, 0f, texture.width, texture.height);
            var spritePivot = pivot ?? new Vector2(0.5f, 0.5f);
            return Sprite.Create(texture, rect, spritePivot, pixelsPerUnit);
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

        private sealed class DecodeInput
        {
            public DecodeInput(byte[] imageBytes, TextureOptions options)
            {
                ImageBytes = imageBytes ?? throw new ArgumentNullException(nameof(imageBytes));
                Options = options ?? throw new ArgumentNullException(nameof(options));
            }

            public byte[] ImageBytes { get; }
            public TextureOptions Options { get; }
            public bool LastDecodeUsedThreaded { get; set; }
            public bool LastDecodeFellBackToUnity { get; set; }
        }
    }
}
