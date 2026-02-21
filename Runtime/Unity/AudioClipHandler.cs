using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Unity.Decoders;
using UnityEngine;
using UnityEngine.Networking;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Supported audio formats for Unity decode interop.
    /// </summary>
    public enum AudioClipType
    {
        WAV,
        MP3,
        OGG,
        AIFF
    }

    /// <summary>
    /// Runtime policy for audio clip decode and temp-file behavior.
    /// </summary>
    public sealed class AudioClipPipelineOptions
    {
        public int MaxConcurrentDecodes { get; set; } = 2;
        public int MaxActiveTempFiles { get; set; } = 128;
        public int TempShardCount { get; set; } = 32;
        public int TempMaxConcurrentIo { get; set; } = 2;
        public int CleanupRetryCount { get; set; } = 3;
        public TimeSpan CleanupRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        public bool EnableThreadedDecode { get; set; }
        public int ThreadedDecodeMinBytes { get; set; } = 512 * 1024;

        public bool EnableStreamingForLargeClips { get; set; } = true;
        public int StreamingThresholdBytes { get; set; } = 2 * 1024 * 1024;

        public AudioClipPipelineOptions Clone()
        {
            return new AudioClipPipelineOptions
            {
                MaxConcurrentDecodes = MaxConcurrentDecodes,
                MaxActiveTempFiles = MaxActiveTempFiles,
                TempShardCount = TempShardCount,
                TempMaxConcurrentIo = TempMaxConcurrentIo,
                CleanupRetryCount = CleanupRetryCount,
                CleanupRetryDelay = CleanupRetryDelay,
                EnableThreadedDecode = EnableThreadedDecode,
                ThreadedDecodeMinBytes = ThreadedDecodeMinBytes,
                EnableStreamingForLargeClips = EnableStreamingForLargeClips,
                StreamingThresholdBytes = StreamingThresholdBytes
            };
        }

        public void Validate()
        {
            if (MaxConcurrentDecodes < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentDecodes));
            if (MaxActiveTempFiles < 1) throw new ArgumentOutOfRangeException(nameof(MaxActiveTempFiles));
            if (TempShardCount < 1) throw new ArgumentOutOfRangeException(nameof(TempShardCount));
            if (TempMaxConcurrentIo < 1) throw new ArgumentOutOfRangeException(nameof(TempMaxConcurrentIo));
            if (CleanupRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(CleanupRetryCount));
            if (CleanupRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CleanupRetryDelay));
            if (ThreadedDecodeMinBytes < 1) throw new ArgumentOutOfRangeException(nameof(ThreadedDecodeMinBytes));
            if (StreamingThresholdBytes < 1) throw new ArgumentOutOfRangeException(nameof(StreamingThresholdBytes));
        }
    }

    /// <summary>
    /// Unity AudioClip helpers for <see cref="UHttpResponse"/> and <see cref="UHttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Unity format support can vary by platform/build target. Unsupported combinations
    /// fail during decode and surface as deterministic exceptions.
    /// </remarks>
    public static class AudioClipHandler
    {
        private static readonly object OptionsLock = new object();
        private static AudioClipPipelineOptions _options = new AudioClipPipelineOptions();
        private static SemaphoreSlim _decodeLimiter = new SemaphoreSlim(
            _options.MaxConcurrentDecodes,
            _options.MaxConcurrentDecodes);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (OptionsLock)
            {
                _options = new AudioClipPipelineOptions();
                var oldLimiter = _decodeLimiter;
                _decodeLimiter = new SemaphoreSlim(
                    _options.MaxConcurrentDecodes,
                    _options.MaxConcurrentDecodes);
                oldLimiter?.Dispose();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RuntimeInitialize()
        {
            EnsureStartupCleanup();
        }

        public static AudioClipPipelineOptions GetOptions()
        {
            lock (OptionsLock)
            {
                return _options.Clone();
            }
        }

        public static void Configure(AudioClipPipelineOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var clone = options.Clone();
            clone.Validate();

            lock (OptionsLock)
            {
                var previousMax = _options.MaxConcurrentDecodes;
                _options = clone;

                if (clone.MaxConcurrentDecodes != previousMax)
                {
                    var oldLimiter = _decodeLimiter;
                    _decodeLimiter = new SemaphoreSlim(
                        clone.MaxConcurrentDecodes,
                        clone.MaxConcurrentDecodes);
                    oldLimiter?.Dispose();
                }
            }

            UnityTempFileManager.Shared.Configure(new UnityTempFileManagerOptions
            {
                ShardCount = clone.TempShardCount,
                MaxActiveFiles = clone.MaxActiveTempFiles,
                MaxConcurrentIo = clone.TempMaxConcurrentIo,
                CleanupRetryCount = clone.CleanupRetryCount,
                CleanupRetryDelay = clone.CleanupRetryDelay
            });
        }

        /// <summary>
        /// Converts response bytes into an AudioClip by decoding through managed decoder or Unity's audio loader fallback.
        /// </summary>
        public static async Task<AudioClip> AsAudioClipAsync(
            this UHttpResponse response,
            AudioClipType audioType,
            string clipName = "clip",
            CancellationToken cancellationToken = default)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (string.IsNullOrWhiteSpace(clipName))
                throw new ArgumentException("Clip name cannot be null or empty.", nameof(clipName));
            if (response.Body.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Cannot decode AudioClip because response body is empty.");
            }

            EnsureStartupCleanup();
            cancellationToken.ThrowIfCancellationRequested();

            var options = GetOptions();
            var limiter = GetDecodeLimiter();

            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (options.EnableThreadedDecode &&
                    response.Body.Length >= options.ThreadedDecodeMinBytes)
                {
                    var managedClip = await TryManagedDecodeAsync(
                        response,
                        audioType,
                        clipName,
                        cancellationToken).ConfigureAwait(false);

                    if (managedClip != null)
                        return managedClip;
                }

                return await DecodeViaUnityTempPathAsync(
                        response,
                        audioType,
                        clipName,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                limiter.Release();
            }
        }

        /// <summary>
        /// Downloads audio and converts it into an AudioClip.
        /// </summary>
        public static async Task<AudioClip> GetAudioClipAsync(
            this UHttpClient client,
            string url,
            AudioClipType audioType,
            string clipName = "clip",
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            using var response = await client
                .Get(url)
                .WithHeader("Accept", BuildAcceptHeader(audioType))
                .SendAsync(cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            return await response
                .AsAudioClipAsync(audioType, clipName, cancellationToken)
                .ConfigureAwait(false);
        }

        public static UnityTempFileManagerMetrics GetTempFileMetrics()
        {
            return UnityTempFileManager.Shared.GetMetrics();
        }

        private static SemaphoreSlim GetDecodeLimiter()
        {
            lock (OptionsLock)
            {
                return _decodeLimiter;
            }
        }

        private static async Task<AudioClip> TryManagedDecodeAsync(
            UHttpResponse response,
            AudioClipType audioType,
            string clipName,
            CancellationToken cancellationToken)
        {
            var contentType = response.Headers?.Get("Content-Type");
            var sourcePath = response.Request?.Uri?.AbsolutePath ?? GetExtension(audioType);

            if (!DecoderRegistry.TryResolveAudioDecoder(
                    contentType,
                    sourcePath,
                    out var decoder,
                    out var reason))
            {
                Debug.LogWarning(
                    "[TurboHTTP] Managed audio decode unavailable (" + reason + "). " +
                    "Falling back to Unity decode temp-file path.");
                return null;
            }

            try
            {
                var decoded = await decoder
                    .DecodeAsync(response.Body, cancellationToken)
                    .ConfigureAwait(false);

                return await MainThreadDispatcher.ExecuteAsync(
                    () => CreateAudioClip(decoded, clipName),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TurboHTTP] Managed audio decode failed and will fall back to Unity decode path. " +
                    "Error: " + ex.Message);
                return null;
            }
        }

        private static AudioClip CreateAudioClip(DecodedAudio decoded, string clipName)
        {
            if (decoded == null) throw new ArgumentNullException(nameof(decoded));

            var clip = AudioClip.Create(
                clipName,
                decoded.SampleFrames,
                decoded.Channels,
                decoded.SampleRate,
                stream: false);

            if (!clip.SetData(decoded.Samples, 0))
            {
                UnityEngine.Object.Destroy(clip);
                throw new InvalidOperationException("Failed to push decoded PCM data into AudioClip.");
            }

            clip.name = clipName;
            return clip;
        }

        private static async Task<AudioClip> DecodeViaUnityTempPathAsync(
            UHttpResponse response,
            AudioClipType audioType,
            string clipName,
            AudioClipPipelineOptions options,
            CancellationToken cancellationToken)
        {
            if (!UnityTempFileManager.Shared.TryReservePath(GetExtension(audioType), out var tempPath))
            {
                throw new InvalidOperationException(
                    "Audio temp-file manager reached MaxActiveTempFiles limit.");
            }

            try
            {
                await UnityTempFileManager.Shared
                    .WriteBytesAsync(tempPath, response.Body, cancellationToken)
                    .ConfigureAwait(false);

                var shouldStream =
                    options.EnableStreamingForLargeClips &&
                    response.Body.Length >= options.StreamingThresholdBytes;

                return await LoadAudioClipFromFileAsync(
                        tempPath,
                        audioType,
                        clipName,
                        shouldStream,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                UnityTempFileManager.Shared.ReleaseAndScheduleDelete(tempPath);
            }
        }

        private static async Task<AudioClip> LoadAudioClipFromFileAsync(
            string filePath,
            AudioClipType audioType,
            string clipName,
            bool streamAudio,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<AudioClip>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            try
            {
                await MainThreadDispatcher.ExecuteControlAsync(() =>
                {
                    var fileUri = new Uri(filePath).AbsoluteUri;
                    var unityAudioType = ToUnityAudioType(audioType);

                    MainThreadDispatcher.Instance.StartCoroutine(
                        LoadAudioClipCoroutine(
                            fileUri,
                            unityAudioType,
                            clipName,
                            streamAudio,
                            cancellationToken,
                            tcs));
                }, cancellationToken).ConfigureAwait(false);

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        private static IEnumerator LoadAudioClipCoroutine(
            string fileUri,
            UnityEngine.AudioType unityAudioType,
            string clipName,
            bool streamAudio,
            CancellationToken cancellationToken,
            TaskCompletionSource<AudioClip> tcs)
        {
            using (var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, unityAudioType))
            {
                if (request.downloadHandler is DownloadHandlerAudioClip downloadHandler)
                {
                    downloadHandler.streamAudio = streamAudio;
                }

                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        tcs.TrySetCanceled(cancellationToken);
                        yield break;
                    }

                    yield return null;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "Failed to decode audio clip from temp file '" +
                        fileUri +
                        "'. Error: " +
                        request.error));
                    yield break;
                }

                try
                {
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null)
                    {
                        tcs.TrySetException(new InvalidOperationException(
                            "Audio decoding returned a null AudioClip."));
                        yield break;
                    }

                    clip.name = clipName;
                    tcs.TrySetResult(clip);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "Failed to finalize decoded AudioClip.",
                        ex));
                }
            }
        }

        private static UnityEngine.AudioType ToUnityAudioType(AudioClipType audioType)
        {
            switch (audioType)
            {
                case AudioClipType.WAV:
                    return UnityEngine.AudioType.WAV;
                case AudioClipType.MP3:
                    return UnityEngine.AudioType.MPEG;
                case AudioClipType.OGG:
                    return UnityEngine.AudioType.OGGVORBIS;
                case AudioClipType.AIFF:
                    return UnityEngine.AudioType.AIFF;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(audioType),
                        audioType,
                        "Unsupported audio format. Supported values: WAV, MP3, OGG, AIFF.");
            }
        }

        private static string BuildAcceptHeader(AudioClipType audioType)
        {
            switch (audioType)
            {
                case AudioClipType.WAV:
                    return "audio/wav, audio/x-wav";
                case AudioClipType.MP3:
                    return "audio/mpeg, audio/mp3";
                case AudioClipType.OGG:
                    return "audio/ogg, application/ogg";
                case AudioClipType.AIFF:
                    return "audio/aiff, audio/x-aiff";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(audioType),
                        audioType,
                        "Unsupported audio format. Supported values: WAV, MP3, OGG, AIFF.");
            }
        }

        private static string GetExtension(AudioClipType audioType)
        {
            switch (audioType)
            {
                case AudioClipType.WAV:
                    return ".wav";
                case AudioClipType.MP3:
                    return ".mp3";
                case AudioClipType.OGG:
                    return ".ogg";
                case AudioClipType.AIFF:
                    return ".aiff";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(audioType),
                        audioType,
                        "Unsupported audio format. Supported values: WAV, MP3, OGG, AIFF.");
            }
        }

        private static void EnsureStartupCleanup()
        {
            UnityTempFileManager.Shared.EnsureStartupSweep();
        }
    }
}
