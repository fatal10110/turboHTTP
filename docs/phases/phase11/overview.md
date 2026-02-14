# Phase 11 Implementation Plan - Overview

Phase 11 is broken into 5 sub-phases. Main-thread coordination is built first, then content handlers and Unity API helpers, followed by coroutine compatibility wrappers.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [11.1](phase-11.1-main-thread-dispatcher.md) | Main Thread Dispatcher | 1 new | Phase 10 |
| [11.2](phase-11.2-texture2d-handler.md) | Texture2D and Sprite Handlers | 1 new | 11.1 |
| [11.3](phase-11.3-audioclip-handler.md) | AudioClip Handler | 1 new | 11.1 |
| [11.4](phase-11.4-unity-helper-extensions.md) | Unity Helper Extensions | 1 new | 11.2, 11.3 |
| [11.5](phase-11.5-coroutine-wrapper.md) | Coroutine Wrapper API | 1 new | 11.1, 11.4 |

## Dependency Graph

```text
Phase 10 (done)
    └── 11.1 Main Thread Dispatcher
         ├── 11.2 Texture2D and Sprite Handlers
         └── 11.3 AudioClip Handler
              └── 11.4 Unity Helper Extensions
                   └── 11.5 Coroutine Wrapper API
```

Sub-phases 11.2 and 11.3 can run in parallel once 11.1 is complete.

## Existing Foundation (Phases 5 + 7 + 10)

### Existing Types Used in Phase 11

| Type | Key APIs for Phase 11 |
|------|----------------------|
| `UHttpClient` | request and typed helper entry points |
| `UHttpResponse` | body conversion into Unity-native assets |
| `UHttpRequestBuilder` | coroutine wrapper send entry point |
| `RequestContext` | timeline capture during Unity-specific flows |
| Cache/RateLimit middleware | production middleware compatibility with Unity helpers |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Unity` | Core, Files, JSON | false | Unity-specific helpers and handlers |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | integration and behavior tests |

## Cross-Cutting Design Decisions

1. Unity API calls must execute on main thread only.
2. Main-thread dispatch API must be async-friendly and avoid busy waits.
3. Asset handler cleanup (textures, temp audio files) must be deterministic.
4. Coroutine wrappers must preserve exception and cancellation semantics.
5. Unity helper APIs should be optional convenience layers over existing core APIs.

## All Files (5 new)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Unity/MainThreadDispatcher.cs` | Unity |
| 2 | Create | `Runtime/Unity/Texture2DHandler.cs` | Unity |
| 3 | Create | `Runtime/Unity/AudioClipHandler.cs` | Unity |
| 4 | Create | `Runtime/Unity/UnityExtensions.cs` | Unity |
| 5 | Create | `Runtime/Unity/CoroutineWrapper.cs` | Unity |

## Post-Implementation

1. Validate runtime behavior on Editor and IL2CPP players.
2. Run Unity integration scene and automated runtime tests.
3. Validate no main-thread deadlocks under stress and cancellation.
4. Run specialist reviews before advancing to Phase 12.
