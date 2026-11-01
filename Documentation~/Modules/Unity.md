# Unity Module

The `TurboHTTP.Unity` module integrates TurboHTTP's background networking with the Unity Engine: main-thread dispatch, texture, sprite and audio decoding, coroutines, file downloads to Unity paths, and mobile networking policies.

## MainThreadDispatcher

All network I/O in TurboHTTP runs on background thread pool threads, so a continuation after `await` may not run on the main thread, and Unity objects (like a `Texture2D`) cannot be created there. TurboHTTP handles this for its own Unity helpers; dispatch manually for everything else:

<!-- snippet: with GameObject myGameObject = null; -->
```csharp
using TurboHTTP.Unity;

MainThreadDispatcher.Enqueue(() => myGameObject.SetActive(true));
```

## Content handlers

Extension methods on `UHttpClient` download and decode common Unity asset types. The download runs on background threads and the Unity object is created on the main thread; `TextureOptions.EnableThreadedDecode` moves the pixel decoding off the main thread too.

### Textures and sprites

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Unity;

Texture2D tex = await client.GetTextureAsync("https://example.com/image.png");
Sprite sprite = await client.GetSpriteAsync("https://example.com/image.png");
```

`TextureOptions` bounds the work (`MaxBodyBytes`, `MaxPixels`, concurrent decodes) so a hostile image cannot exhaust memory. To decode a response you already have, use `response.AsTexture2D()`, and `texture.AsSprite()` for a sprite.

### Audio clips

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Unity;

AudioClip clip = await client.GetAudioClipAsync("https://example.com/sound.mp3", AudioClipType.MP3);
```

`AudioClipType` is `WAV`, `MP3`, `OGG` or `AIFF`. Tune decoding and temp-file behavior with `AudioClipHandler.Configure(new AudioClipPipelineOptions { ... })`.

### Downloads to Unity paths

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Unity;

string path = await client.DownloadToPersistentDataAsync("https://example.com/pack.zip", "packs/pack.zip");
string cached = await client.DownloadToTempCacheAsync("https://example.com/pack.zip", "pack.zip");
```

The relative path is validated: it cannot leave `Application.persistentDataPath` or the temp cache. The file is written atomically.

### AssetBundles

TurboHTTP does not load AssetBundles. Download the bundle with `DownloadToPersistentDataAsync` (or `FileDownloader`) and load it from disk with `AssetBundle.LoadFromFileAsync`. A dedicated AssetBundle handler is tracked in #122.

## Coroutine integration

If you prefer coroutines over `async`/`await`, `SendCoroutine` and `GetJsonCoroutine<T>` wrap a request and deliver the result on the main thread through callbacks.

<!-- snippet: with UHttpClient client = null; -->
```csharp
using System.Collections;
using TurboHTTP.Unity;

IEnumerator LoadData()
{
    yield return client.Get("https://api.example.com/data").SendCoroutine(
        onSuccess: response =>
        {
            Debug.Log($"Loaded: {response.StatusCode}");
            response.Dispose();
        },
        onError: ex => Debug.LogError(ex.Message));
}
```

The callback owns the response and disposes it. Pass `callbackOwner` and `cancelOnOwnerInactive: true` to cancel the request when a `MonoBehaviour` is disabled or destroyed.

## Mobile networking

`options.UseAdaptiveNetworking(new AdaptivePolicy { Enable = true, ... })` adds an interceptor that adjusts timeouts (and, optionally, concurrency and retries) from the measured network quality. `options.UseBackgroundNetworking(new BackgroundNetworkingPolicy { Enable = true })` queues requests when the app is paused on iOS and Android. Background networking is experimental and needs the `TURBOHTTP_BACKGROUND_NETWORKING` define; see PlatformNotes.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Unity.Mobile;

options.UseAdaptiveNetworking(new AdaptivePolicy
{
    Enable = true,
    MinTimeout = TimeSpan.FromSeconds(2),
    MaxTimeout = TimeSpan.FromSeconds(30)
});
```

## CreateUnityClient

`UnityExtensions.CreateUnityClient(configure)` creates a client with Unity-friendly defaults (a `User-Agent` header) and lets you adjust the options.
