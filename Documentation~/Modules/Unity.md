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

Extension methods on `UHttpClient` download and decode common Unity asset types. The download runs on background threads and the Unity object is created on the main thread; pixel decoding currently runs on the main thread: `TextureOptions.EnableThreadedDecode` falls back to it with a warning until the managed decoders (StbImageSharp, NVorbis, NLayer; defines `TURBOHTTP_STBIMAGESHARP`, `TURBOHTTP_NVORBIS`, `TURBOHTTP_NLAYER`) are implemented (#158). To decode off the main thread now, register your own `IImageDecoder` at startup, before the first decode (`DecoderRegistry.Initialize(b => b.AddImageDecoder(...))` or `RegisterImageDecoder`; it throws once the registry is sealed), and set `TextureOptions.EnableThreadedDecode = true`. See [Modules and manual registration](ModulesAndRegistration.md).

### Textures and sprites

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Unity;

Texture2D tex = await client.GetTextureAsync("https://example.com/image.png");
Sprite sprite = await client.GetSpriteAsync("https://example.com/image.png");
```

`TextureOptions` bounds the work so a hostile image cannot exhaust memory: `MaxSourceBytes` (alias `MaxBodyBytes`) defaults to 64 MiB and `MaxPixels` to 4096 x 4096. The pixel limit is checked against the PNG or JPEG header before decoding; while it is set, an image whose header dimensions cannot be read is rejected with `InvalidOperationException`. That includes formats other than PNG and JPEG handled by a custom `IImageDecoder` registered through `DecoderRegistry.RegisterImageDecoder`: such a decoder needs `MaxPixels = null` (keep `MaxSourceBytes` set, and bound the decoded size inside the decoder). Set either limit to `null` to remove it. Concurrent decodes are bounded by `TextureDecodeScheduler`. To decode a response you already have, use `response.AsTexture2D()`, and `texture.AsSprite()` for a sprite.

### Audio clips

<!-- snippet: with UHttpClient client = null; -->
```csharp
using TurboHTTP.Unity;

AudioClip clip = await client.GetAudioClipAsync("https://example.com/sound.mp3", AudioClipType.MP3);
```

`AudioClipType` is `WAV`, `MP3`, `OGG` or `AIFF`. Tune decoding and temp-file behavior with `AudioClipHandler.Configure(new AudioClipPipelineOptions { ... })`. Two bounds are on by default, and both reject with `InvalidOperationException` before a temp file is written:

- `MaxSourceBytes` (64 MiB) limits the encoded payload.
- `MaxDecodedSamples` (32 Mi samples, frames x channels) limits what is decoded into memory. A WAV or AIFF clip that is not streamed is checked against the sample count its header declares. MP3 and OGG declare no reliable decoded size, so while the limit is set they are never decoded to PCM up front: clips at or above `StreamingThresholdBytes` stream as before (a streamed clip keeps its temp file until it is destroyed), and smaller ones load compressed in memory, bounded by `MaxSourceBytes`, and release their temp file once loaded.

Set either one to `null` to remove it; `MaxDecodedSamples = null` restores full PCM decoding of compressed clips below `StreamingThresholdBytes`.

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

If you prefer coroutines over `async`/`await`, `SendCoroutine` and `GetJsonCoroutine<T>` wrap a request and deliver the result on the main thread through callbacks. `GetJsonCoroutine<T>` uses the registered JSON serializer ([JSON serializers](../APIReference.md#json-serializers)).

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

The callback owns the response and disposes it. A response the callback never receives (callbacks suppressed, no `onSuccess`, coroutine disposed) is disposed for you. Pass `callbackOwner` to cancel the request when that object is destroyed, and add `cancelOnOwnerInactive: true` to cancel it when its GameObject is deactivated.

Unity never disposes a coroutine it stops, so the wrapper cannot see `StopCoroutine` or `StopAllCoroutines` by itself. The returned enumerator implements `IDisposable`: when you stop the coroutine yourself, dispose it as well. That cancels the request and disposes a response that was not delivered.

<!-- snippet: with UHttpClient client = null; -->
<!-- snippet: with MonoBehaviour host = null; -->
```csharp
using System;
using TurboHTTP.Unity;

var request = client.Get("https://api.example.com/data").SendCoroutine(
    onSuccess: response => response.Dispose(),
    callbackOwner: host);
host.StartCoroutine(request);

// Later, to cancel it:
host.StopCoroutine(request);
(request as IDisposable)?.Dispose();
```

## Mobile networking

`options.UseAdaptiveNetworking(new AdaptivePolicy { Enable = true, ... })` adds an interceptor that adapts request timeouts from the measured network quality (timeouts only). It touches only requests without an explicit timeout, and the adapted value is clamped to `[MinTimeout, MaxTimeout]` (defaults 2 s and 30 s), so the default `MaxTimeout` can shorten the 45 s mobile default timeout; raise `MaxTimeout` to keep it. `options.UseBackgroundNetworking(new BackgroundNetworkingPolicy { Enable = true })` extends a request's lifetime with the platform background-execution grace period when the app is paused on iOS and Android. A replayable request that expires and fits the bounded in-memory queue (`MaxQueuedRequests`, default 256) is copied into it and fails with `BackgroundRequestQueuedException`; any other request rethrows its original cancellation; nothing in the library replays that queue yet (#984), so retry it yourself. Background networking is experimental and needs the `TURBOHTTP_BACKGROUND_NETWORKING` define; see PlatformNotes.

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
