# Phase 24.2: AssetBundle Handler

**Depends on:** Phase 15
**Estimated Effort:** 2-3 weeks

---

## Step 0: Define API Surface and Ownership Model

Required behavior:

1. Define `AssetBundleHandler` static APIs on response/client surfaces (`AsAssetBundleAsync`, `GetAssetBundleAsync`).
2. Define a wrapper type that owns the loaded `AssetBundle` and lifecycle rules.
3. Define configuration knobs: memory-vs-temp-file threshold, maximum download size, integrity verification options.

Implementation constraints:

1. API should align with existing Unity handler conventions.
2. Ownership semantics must be explicit: dispose should call `Unload(false)` and release temp resources safely.
3. WebGL unsupported behavior must be explicit and deterministic.

---

## Step 1: Implement Download and Routing Pipeline

Required behavior:

1. Download response body with cancellation/progress support.
2. Route payload to memory or temp file based on size threshold (default 16 MB).
3. Enforce maximum download size guard (default 512 MB).
4. Use `UnityTempFileManager` for all temp-file creation and cleanup tracking.

Implementation constraints:

1. Size threshold logic must be deterministic and configurable.
2. Large payload paths must avoid accidental full duplication in memory.
3. Guard failures must return actionable error messages.

---

## Step 2: Implement Bundle Load and Main-Thread Dispatch

Required behavior:

1. Load from memory (`AssetBundle.LoadFromMemoryAsync`) or file (`AssetBundle.LoadFromFileAsync`).
2. Dispatch Unity main-thread-only calls through `MainThreadDispatcher`.
3. Support optional CRC/hash verification when loading from file.

Implementation constraints:

1. Main-thread dispatch must preserve cancellation/error propagation behavior.
2. File-backed bundle path must keep file alive while bundle remains loaded.
3. Load errors must map to predictable handler exceptions/results.

---

## Step 3: Lifecycle and Cleanup Hardening

Required behavior:

1. Ensure `Unload(false)` is performed on wrapper disposal.
2. Defer temp-file release until bundle unload completes.
3. Support crash recovery/cleanup via `UnityTempFileManager` policies.

Implementation constraints:

1. No premature temp-file deletion while the bundle is still in use.
2. Dispose path must be idempotent and safe under repeated calls.
3. Cleanup behavior must be robust under cancellation and failed loads.

---

## Step 4: Cache and Compatibility Documentation

Required behavior:

1. Document interaction with `CacheMiddleware` (raw bytes caching + re-decode on hit).
2. Document non-integration with Unity `Caching` API for this client architecture.
3. Publish platform support matrix and WebGL alternative guidance.

Implementation constraints:

1. Documentation must call out the temp-file lifetime requirement for file-backed bundles.
2. Unsupported/platform-specific behavior must include recommended fallback path.

---

## Verification Criteria

1. Memory route and file route both load bundles correctly.
2. Max-size guard prevents oversized downloads.
3. Main-thread loading behavior is deterministic and thread-safe.
4. Bundle wrapper disposal unloads bundle and cleans resources correctly.
5. Temp-file cleanup is deferred correctly until unload.
6. Integration tests cover success, invalid payload, cancellation, and cleanup paths.
