# Phase 11.2: Texture2D and Sprite Handlers

**Depends on:** Phase 11.1
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Implement Texture Conversion APIs

**File:** `Runtime/Unity/Texture2DHandler.cs`

Required behavior:

1. Provide `AsTexture2D` conversion for image response bytes.
2. Provide `GetTextureAsync` for download + conversion flow.
3. Provide sprite helper APIs (`AsSprite`, `GetSpriteAsync`).
4. Support configurable texture options (readability, mipmaps, format, linear).

Implementation constraints:

1. All Unity object creation and mutation must run on main thread.
2. Validate empty/invalid response body and fail with clear errors.
3. Validate `Content-Type` starts with `image/` by default (with explicit opt-out for non-compliant APIs).
4. Preserve cancellation semantics through request pipeline before decode begins.
5. If response body may come from pooled buffers, copy bytes before main-thread dispatch to guarantee buffer lifetime during decode.
6. Document ownership explicitly: caller is responsible for destroying returned `Texture2D`/`Sprite` when no longer used.
7. Avoid hidden global texture caches in this phase.

---

## Verification Criteria

1. PNG/JPEG bodies convert to valid `Texture2D` instances.
2. Sprite creation from downloaded textures succeeds on main thread.
3. Invalid image data fails deterministically with actionable exceptions.
4. Texture option flags are applied to produced textures.
5. Non-image `Content-Type` is rejected by default.
