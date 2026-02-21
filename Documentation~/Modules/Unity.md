# Unity Module

The `Unity` module provides seamless integration between TurboHTTP's background async networking and the Unity Engine's main thread concepts (`UnityEngine.Object` creation, Coroutines, etc.).

## MainThreadDispatcher

Since all network I/O in TurboHTTP runs on background thread pool threads (to avoid hitching the game loop), you cannot create Unity Objects (like `Texture2D`) directly from a background continuation without dispatching.

TurboHTTP handles this automatically for its higher-level extensions, but you can dispatch manually:

```csharp
using TurboHTTP.Unity;

MainThreadDispatcher.Enqueue(() => {
    myGameObject.SetActive(true);
});
```

## Content Handlers

Extension methods exist directly on `UHttpClient` for common Unity asset types:

### Textures & Sprites

```csharp
// Downloads bytes in background, dispatches Texture2D.LoadImage to Main Thread, returns Texture2D.
Texture2D tex = await client.GetTextureAsync("https://example.com/image.png");

// Downloads bytes and creates a Sprite.
Sprite sprite = await client.GetSpriteAsync("https://example.com/image.png");
```

### AudioClips

```csharp
// Downloads audio, uses UnityWebRequestMultimedia under the hood ONLY for the audio decoding step on the main thread, 
// or custom decoders where applicable.
AudioClip clip = await client.GetAudioClipAsync("https://example.com/sound.mp3", AudioType.MPEG);
```

### AssetBundles

Downloads an AssetBundle efficiently.

```csharp
AssetBundle bundle = await client.GetAssetBundleAsync("https://example.com/assets.bundle");
```

### Advanced Handlers (GLTF, Video) 

Phase 20 introduces glTF and Video handler stubs that integrate with rendering pipelines efficiently.

```csharp
// Example (if glTF module installed)
// GameObject model = await client.GetGltfAsync("https://example.com/model.gltf");
```

## Coroutine Integration

If you prefer Coroutines over `async`/`await`, you can yield on a request builder.

```csharp
public IEnumerator LoadData()
{
    var request = client.Get("/data").SendAsync();
    
    // Yield instruction built into TurboHTTP
    yield return request.ToCoroutine();
    
    if (request.Result.IsSuccessStatusCode)
    {
        Debug.Log("Loaded!");
    }
}
```
