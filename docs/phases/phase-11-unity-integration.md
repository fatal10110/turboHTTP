# Phase 11: Unity Integration

**Milestone:** M3 (v1.0 "feature-complete + release")
**Dependencies:** Phase 10 (Advanced Middleware)
**Estimated Complexity:** Medium
**Critical:** Yes - Unity-specific features

## Overview

Implement Unity-specific integrations that make TurboHTTP feel native to Unity: Texture2D and AudioClip content handlers, main thread synchronization for Unity API calls, and Unity-friendly helper methods.

Detailed sub-phase breakdown: [Phase 11 Implementation Plan - Overview](phase11/overview.md)

This phase targets a compatibility-first baseline. Advanced scale/correctness hardening (dispatcher backpressure, decode scheduling, temp-file lifecycle manager, lifecycle-bound coroutine cancellation, reliability stress gates) is explicitly deferred to [Phase 15](phase-15-unity-runtime-hardening.md).

## Goals

1. Create `Texture2DHandler` for loading images
2. Create `AudioClipHandler` for loading audio
3. Create `MainThreadDispatcher` for Unity API calls
4. Create Unity extension methods
5. Support sprite creation from textures
6. Handle Unity asset lifecycle properly
7. Add coroutine-based API alongside async/await

## Tasks

### Task 11.1: Main Thread Dispatcher

**File:** `Runtime/Unity/MainThreadDispatcher.cs`

```csharp
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Dispatches actions to Unity's main thread.
    /// Required for calling Unity APIs from background threads.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

        /// <summary>
        /// Get or create the singleton instance.
        /// </summary>
        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[TurboHTTP MainThreadDispatcher]");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Update()
        {
            // Execute all queued actions
            while (_actions.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MainThreadDispatcher] Error executing action: {ex}");
                }
            }
        }

        /// <summary>
        /// Enqueue an action to be executed on the main thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null)
                return;

            _actions.Enqueue(action);
        }

        /// <summary>
        /// Execute an action on the main thread and wait for completion.
        /// </summary>
        public static T Execute<T>(Func<T> func)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            // If already on main thread, execute directly
            if (IsMainThread())
            {
                return func();
            }

            T result = default;
            Exception exception = null;
            var completed = false;

            Enqueue(() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    completed = true;
                }
            });

            // Wait for completion
            while (!completed)
            {
                System.Threading.Thread.Sleep(1);
            }

            if (exception != null)
                throw exception;

            return result;
        }

        /// <summary>
        /// Check if currently on Unity's main thread.
        /// </summary>
        public static bool IsMainThread()
        {
            return System.Threading.Thread.CurrentThread.ManagedThreadId == 1;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
```

### Task 11.2: Texture2D Handler

**File:** `Runtime/Unity/Texture2DHandler.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Options for texture loading.
    /// </summary>
    public class TextureOptions
    {
        public bool Readable { get; set; } = false;
        public bool MipMaps { get; set; } = true;
        public bool Linear { get; set; } = false;
        public TextureFormat Format { get; set; } = TextureFormat.RGBA32;
    }

    /// <summary>
    /// Extension methods for loading Texture2D from HTTP responses.
    /// </summary>
    public static class Texture2DHandler
    {
        /// <summary>
        /// Convert response body to Texture2D.
        /// Must be called on main thread or will automatically dispatch.
        /// </summary>
        public static Texture2D AsTexture2D(this UHttpResponse response, TextureOptions options = null)
        {
            options ??= new TextureOptions();

            if (response.Body == null || response.Body.Length == 0)
            {
                throw new InvalidOperationException("Response body is empty");
            }

            return MainThreadDispatcher.Execute(() =>
            {
                var texture = new Texture2D(2, 2, options.Format, options.MipMaps, options.Linear);
                texture.LoadImage(response.Body, !options.Readable);
                return texture;
            });
        }

        /// <summary>
        /// Download and load a texture.
        /// </summary>
        public static async Task<Texture2D> GetTextureAsync(
            this UHttpClient client,
            string url,
            TextureOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await client
                .Get(url)
                .Accept("image/png, image/jpeg, image/jpg")
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return response.AsTexture2D(options);
        }

        /// <summary>
        /// Create a sprite from a texture.
        /// </summary>
        public static Sprite AsSprite(this Texture2D texture, Vector2? pivot = null)
        {
            return MainThreadDispatcher.Execute(() =>
            {
                var rect = new Rect(0, 0, texture.width, texture.height);
                var pivotPoint = pivot ?? new Vector2(0.5f, 0.5f);
                return Sprite.Create(texture, rect, pivotPoint);
            });
        }

        /// <summary>
        /// Download and create a sprite.
        /// </summary>
        public static async Task<Sprite> GetSpriteAsync(
            this UHttpClient client,
            string url,
            TextureOptions options = null,
            Vector2? pivot = null,
            CancellationToken cancellationToken = default)
        {
            var texture = await client.GetTextureAsync(url, options, cancellationToken);
            return texture.AsSprite(pivot);
        }
    }
}
```

**Usage Example:**

```csharp
var client = new UHttpClient();

// Load texture
var texture = await client.GetTextureAsync("https://example.com/image.png");
rawImage.texture = texture;

// Load sprite
var sprite = await client.GetSpriteAsync("https://example.com/icon.png");
image.sprite = sprite;

// Manual conversion
var response = await client.Get("https://example.com/photo.jpg").SendAsync();
var texture = response.AsTexture2D(new TextureOptions
{
    Readable = false,
    MipMaps = true
});
```

### Task 11.3: AudioClip Handler

**File:** `Runtime/Unity/AudioClipHandler.cs`

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Audio type for loading.
    /// </summary>
    public enum AudioType
    {
        WAV,
        MP3,
        OGG,
        AIFF
    }

    /// <summary>
    /// Extension methods for loading AudioClip from HTTP responses.
    /// </summary>
    public static class AudioClipHandler
    {
        /// <summary>
        /// Load AudioClip from response bytes.
        /// Uses UnityWebRequestMultimedia internally for audio decoding only (not HTTP transport).
        /// This is necessary because Unity does not expose a public API to create AudioClip from raw bytes.
        /// </summary>
        public static async Task<AudioClip> AsAudioClipAsync(
            this UHttpResponse response,
            AudioType audioType,
            string clipName = "clip",
            CancellationToken cancellationToken = default)
        {
            if (response.Body == null || response.Body.Length == 0)
            {
                throw new InvalidOperationException("Response body is empty");
            }

            // Save to temporary file
            var tempPath = Path.Combine(Application.temporaryCachePath, $"{Guid.NewGuid()}.tmp");
            File.WriteAllBytes(tempPath, response.Body);

            try
            {
                return await LoadAudioClipFromFileAsync(tempPath, audioType, clipName, cancellationToken);
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        /// <summary>
        /// Download and load an audio clip.
        /// </summary>
        public static async Task<AudioClip> GetAudioClipAsync(
            this UHttpClient client,
            string url,
            AudioType audioType,
            string clipName = "clip",
            CancellationToken cancellationToken = default)
        {
            var response = await client
                .Get(url)
                .SendAsync(cancellationToken);

            response.EnsureSuccessStatusCode();
            return await response.AsAudioClipAsync(audioType, clipName, cancellationToken);
        }

        private static async Task<AudioClip> LoadAudioClipFromFileAsync(
            string filePath,
            AudioType audioType,
            string clipName,
            CancellationToken cancellationToken)
        {
            var fileUrl = "file://" + filePath;
            var unityAudioType = ConvertAudioType(audioType);

            AudioClip clip = null;

            await MainThreadDispatcher.Instance.StartCoroutine(
                LoadAudioClipCoroutine(fileUrl, unityAudioType, clipName, result => clip = result)
            );

            return clip;
        }

        private static System.Collections.IEnumerator LoadAudioClipCoroutine(
            string url,
            UnityEngine.AudioType audioType,
            string clipName,
            Action<AudioClip> callback)
        {
            using (var www = UnityWebRequestMultimedia.GetAudioClip(url, audioType))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(www);
                    clip.name = clipName;
                    callback?.Invoke(clip);
                }
                else
                {
                    Debug.LogError($"Failed to load audio clip: {www.error}");
                    callback?.Invoke(null);
                }
            }
        }

        private static UnityEngine.AudioType ConvertAudioType(AudioType audioType)
        {
            return audioType switch
            {
                AudioType.WAV => UnityEngine.AudioType.WAV,
                AudioType.MP3 => UnityEngine.AudioType.MPEG,
                AudioType.OGG => UnityEngine.AudioType.OGGVORBIS,
                AudioType.AIFF => UnityEngine.AudioType.AIFF,
                _ => UnityEngine.AudioType.UNKNOWN
            };
        }
    }

    /// <summary>
    /// Extension to run coroutines from non-MonoBehaviour classes.
    /// </summary>
    public static class CoroutineExtensions
    {
        public static Task StartCoroutine(this MonoBehaviour monoBehaviour, System.Collections.IEnumerator coroutine)
        {
            var tcs = new TaskCompletionSource<bool>();

            monoBehaviour.StartCoroutine(RunCoroutine(coroutine, tcs));

            return tcs.Task;
        }

        private static System.Collections.IEnumerator RunCoroutine(
            System.Collections.IEnumerator coroutine,
            TaskCompletionSource<bool> tcs)
        {
            yield return coroutine;
            tcs.SetResult(true);
        }
    }
}
```

**Usage Example:**

```csharp
var client = new UHttpClient();

// Load audio clip
var audioClip = await client.GetAudioClipAsync(
    "https://example.com/sound.mp3",
    AudioType.MP3,
    "background-music"
);

audioSource.clip = audioClip;
audioSource.Play();
```

### Task 11.4: Unity Helper Extensions

**File:** `Runtime/Unity/UnityExtensions.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Unity-specific extension methods.
    /// </summary>
    public static class UnityExtensions
    {
        /// <summary>
        /// Download content to Unity's persistent data path.
        /// </summary>
        public static Task<string> DownloadToPersistentDataAsync(
            this UHttpClient client,
            string url,
            string filename,
            CancellationToken cancellationToken = default)
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath, filename);
            return DownloadToFileAsync(client, url, path, cancellationToken);
        }

        /// <summary>
        /// Download content to Unity's temporary cache path.
        /// </summary>
        public static Task<string> DownloadToTempCacheAsync(
            this UHttpClient client,
            string url,
            string filename,
            CancellationToken cancellationToken = default)
        {
            var path = System.IO.Path.Combine(Application.temporaryCachePath, filename);
            return DownloadToFileAsync(client, url, path, cancellationToken);
        }

        private static async Task<string> DownloadToFileAsync(
            UHttpClient client,
            string url,
            string path,
            CancellationToken cancellationToken)
        {
            var response = await client.Get(url).SendAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            await System.IO.File.WriteAllBytesAsync(path, response.Body, cancellationToken);
            return path;
        }

        /// <summary>
        /// Create a client optimized for Unity.
        /// Includes common Unity-specific configurations.
        /// </summary>
        public static UHttpClient CreateUnityClient(Action<UHttpClientOptions> configure = null)
        {
            var options = new UHttpClientOptions();

            // Set Unity-specific user agent
            options.DefaultHeaders.Set("User-Agent",
                $"TurboHTTP/1.0 Unity/{Application.unityVersion} {Application.platform}");

            configure?.Invoke(options);

            return new UHttpClient(options);
        }
    }
}
```

### Task 11.5: Coroutine Wrapper

**File:** `Runtime/Unity/CoroutineWrapper.cs`

```csharp
using System;
using System.Collections;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Wrapper to use TurboHTTP with Unity coroutines instead of async/await.
    /// </summary>
    public static class CoroutineWrapper
    {
        /// <summary>
        /// Send a request as a coroutine.
        /// </summary>
        public static IEnumerator SendCoroutine(
            this UHttpRequestBuilder builder,
            Action<UHttpResponse> onSuccess,
            Action<Exception> onError = null)
        {
            var task = builder.SendAsync();

            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception != null)
            {
                onError?.Invoke(task.Exception);
            }
            else
            {
                onSuccess?.Invoke(task.Result);
            }
        }

        /// <summary>
        /// Send a request as a coroutine with typed JSON response.
        /// </summary>
        public static IEnumerator GetJsonCoroutine<T>(
            this UHttpClient client,
            string url,
            Action<T> onSuccess,
            Action<Exception> onError = null)
        {
            var task = client.GetJsonAsync<T>(url);

            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception != null)
            {
                onError?.Invoke(task.Exception);
            }
            else
            {
                onSuccess?.Invoke(task.Result);
            }
        }
    }
}
```

**Usage Example:**

```csharp
public class ExampleBehaviour : MonoBehaviour
{
    void Start()
    {
        // Coroutine-based API
        var client = new UHttpClient();

        StartCoroutine(client.Get("https://api.example.com/data")
            .SendCoroutine(
                onSuccess: response => Debug.Log(response.GetBodyAsString()),
                onError: ex => Debug.LogError(ex.Message)
            ));

        // JSON coroutine
        StartCoroutine(client.GetJsonCoroutine<User[]>(
            "https://api.example.com/users",
            onSuccess: users => Debug.Log($"Got {users.Length} users"),
            onError: ex => Debug.LogError(ex.Message)
        ));
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] `MainThreadDispatcher` correctly queues actions
- [ ] `Texture2D` can be loaded from image URLs
- [ ] `Sprite` can be created from textures
- [ ] `AudioClip` can be loaded from audio URLs
- [ ] Main thread dispatcher works from background threads
- [ ] Unity extensions provide convenient helpers
- [ ] Coroutine wrapper works for legacy codebases

### Manual Testing

Create test scene: `Tests/Runtime/TestUnityIntegration.cs`

```csharp
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.UI;

public class TestUnityIntegration : MonoBehaviour
{
    public RawImage rawImage;
    public Image spriteImage;
    public AudioSource audioSource;

    async void Start()
    {
        await TestTexture();
        await TestSprite();
        await TestAudio();
    }

    async Task TestTexture()
    {
        Debug.Log("=== Test: Load Texture ===");
        var client = UnityExtensions.CreateUnityClient();

        var texture = await client.GetTextureAsync("https://via.placeholder.com/512");
        rawImage.texture = texture;
        Debug.Log($"Loaded texture: {texture.width}x{texture.height}");
    }

    async Task TestSprite()
    {
        Debug.Log("=== Test: Load Sprite ===");
        var client = new UHttpClient();

        var sprite = await client.GetSpriteAsync("https://via.placeholder.com/256");
        spriteImage.sprite = sprite;
        Debug.Log("Loaded sprite");
    }

    async Task TestAudio()
    {
        Debug.Log("=== Test: Load Audio ===");
        var client = new UHttpClient();

        // Use a sample OGG file URL
        var clip = await client.GetAudioClipAsync(
            "https://example.com/sample.ogg",
            AudioType.OGG
        );

        audioSource.clip = clip;
        audioSource.Play();
        Debug.Log("Playing audio");
    }
}
```

## Next Steps

Once Phase 11 is complete and validated:

1. Move to [Phase 12: Editor Tooling](phase-12-editor-tools.md)
2. Implement HTTP Monitor window
3. Add request/response inspection UI
4. M3 milestone near completion

## Notes

- `MainThreadDispatcher` is essential for Unity API calls from background threads
- `Texture2D.LoadImage()` must run on main thread
- AudioClip requires temporary file workaround (Unity limitation)
- Coroutine wrapper provides backward compatibility
- Unity-specific paths (persistentDataPath, temporaryCachePath) are properly supported
