using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
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
    /// Unity AudioClip helpers for <see cref="UHttpResponse"/> and <see cref="UHttpClient"/>.
    /// </summary>
    /// <remarks>
    /// Unity format support can vary by platform/build target. Unsupported combinations
    /// fail during decode and surface as deterministic exceptions.
    /// </remarks>
    public static class AudioClipHandler
    {
        private const string TempDirectoryName = "TurboHTTPAudioDecode";
        private const string TempFilePrefix = "turbohttp-audio-";
        private const int CleanupFailureErrorThreshold = 10;
        private static int _startupCleanupCompleted;
        private static int _cleanupFailureCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Volatile.Write(ref _startupCleanupCompleted, 0);
            Volatile.Write(ref _cleanupFailureCount, 0);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RuntimeInitialize()
        {
            EnsureStartupCleanup();
        }

        /// <summary>
        /// Converts response bytes into an AudioClip by decoding through Unity's audio loader.
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

            var tempPath = GetTempFilePath(audioType);

            try
            {
                await WriteTempFileAsync(tempPath, response.Body, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return await LoadAudioClipFromFileAsync(
                    tempPath,
                    audioType,
                    clipName,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteTempFile(tempPath);
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

            var response = await client
                .Get(url)
                .WithHeader("Accept", BuildAcceptHeader(audioType))
                .SendAsync(cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            cancellationToken.ThrowIfCancellationRequested();

            return await response
                .AsAudioClipAsync(audioType, clipName, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<AudioClip> LoadAudioClipFromFileAsync(
            string filePath,
            AudioClipType audioType,
            string clipName,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<AudioClip>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            try
            {
                await MainThreadDispatcher.ExecuteAsync(() =>
                {
                    var fileUri = new Uri(filePath).AbsoluteUri;
                    var unityAudioType = ToUnityAudioType(audioType);

                    MainThreadDispatcher.Instance.StartCoroutine(
                        LoadAudioClipCoroutine(
                            fileUri,
                            unityAudioType,
                            clipName,
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
            CancellationToken cancellationToken,
            TaskCompletionSource<AudioClip> tcs)
        {
            using (var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, unityAudioType))
            {
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
                        $"Failed to decode audio clip from temp file '{fileUri}'. Error: {request.error}"));
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

        private static async Task WriteTempFileAsync(
            string path,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (payload.IsEmpty)
                    return;

                if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array != null)
                {
                    await file.WriteAsync(
                            segment.Array,
                            segment.Offset,
                            segment.Count,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var copy = payload.ToArray();
                await file.WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string GetTempFilePath(AudioClipType audioType)
        {
            var extension = GetExtension(audioType);
            var fileName = TempFilePrefix + Guid.NewGuid().ToString("N") + extension;
            return Path.Combine(GetTempDirectoryPath(), fileName);
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

        private static string GetTempDirectoryPath()
        {
            return Path.Combine(Application.temporaryCachePath, TempDirectoryName);
        }

        private static void EnsureStartupCleanup()
        {
            if (Interlocked.Exchange(ref _startupCleanupCompleted, 1) != 0)
                return;

            var directory = GetTempDirectoryPath();
            if (!Directory.Exists(directory))
                return;

            try
            {
                var files = Directory.GetFiles(directory, TempFilePrefix + "*");
                for (var i = 0; i < files.Length; i++)
                {
                    TryDeleteTempFile(files[i], suppressWarning: true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TurboHTTP] Startup cleanup for temporary audio decode files failed: " +
                    ex.Message);
            }
        }

        private static void TryDeleteTempFile(string path, bool suppressWarning = false)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Volatile.Write(ref _cleanupFailureCount, 0);
                }
            }
            catch (Exception ex)
            {
                var failureCount = Interlocked.Increment(ref _cleanupFailureCount);

                if (!suppressWarning)
                {
                    Debug.LogWarning(
                        "[TurboHTTP] Could not delete temporary audio decode file '" +
                        path +
                        "'. It will be retried on next startup. Error: " +
                        ex.Message +
                        " (failure count: " +
                        failureCount +
                        ").");
                }

                if (failureCount == CleanupFailureErrorThreshold ||
                    (failureCount > CleanupFailureErrorThreshold &&
                     failureCount % CleanupFailureErrorThreshold == 0))
                {
                    Debug.LogError(
                        "[TurboHTTP] Temporary audio cleanup has failed " +
                        failureCount +
                        " times. Persistent failures can leak storage in " +
                        Application.temporaryCachePath + ".");
                }
            }
        }
    }
}
