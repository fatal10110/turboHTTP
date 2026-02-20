# Phase 15.7: Decoder Provider Matrix and IL2CPP Constraints

**Depends on:** Phase 15.2, Phase 15.3
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 11 new, 1 modified

---

## Step 1: Define Decoder Abstractions and Registry

**Files:**
- `Runtime/Unity/Decoders/IImageDecoder.cs` (new)
- `Runtime/Unity/Decoders/IAudioDecoder.cs` (new)
- `Runtime/Unity/Decoders/DecodedImage.cs` (new)
- `Runtime/Unity/Decoders/DecodedAudio.cs` (new)
- `Runtime/Unity/Decoders/DecoderRegistry.cs` (new)

Required behavior:

1. Define AOT-safe decoder interfaces for worker-thread managed decode.
2. Define decoded payload DTOs carrying dimensions/sample metadata and raw data buffers.
3. Implement explicit decoder registration (no assembly scanning).
4. Add deterministic selection by media type/format and platform policy.
5. Expose capability probes to pipeline handlers without reflection-heavy logic.

Implementation constraints:

1. No runtime code generation or `Reflection.Emit`.
2. Registry must be thread-safe and immutable-after-bootstrap by default.
3. Decoder contracts must support cancellation and deterministic error surfaces.
4. Data models must avoid hidden allocations on hot paths where possible.

---

## Step 2: Implement Default Managed Decoder Providers

**Files:**
- `Runtime/Unity/Decoders/StbImageSharpDecoder.cs` (new)
- `Runtime/Unity/Decoders/WavPcmDecoder.cs` (new)
- `Runtime/Unity/Decoders/AiffPcmDecoder.cs` (new)
- `Runtime/Unity/Decoders/NVorbisDecoder.cs` (new)
- `Runtime/Unity/Decoders/NLayerMp3Decoder.cs` (new)

Required behavior:

1. Provide managed image decode for PNG/JPEG via `StbImageSharp`.
2. Provide in-house PCM parsing for WAV and AIFF.
3. Provide managed OGG/Vorbis decode via `NVorbis`.
4. Provide managed MP3 decode via `NLayer`.
5. Keep AAC/M4A on baseline Unity decode path in this phase.

Implementation constraints:

1. Pin dependency versions (lockfile or explicit package versions) and document license obligations.
2. Decoder errors must include format and parse stage context.
3. Managed decoder path must remain optional and policy-controlled.
4. Keep memory ownership explicit for decoded buffers.

---

## Step 3: Add Platform Routing Policy and Linker Safety

**Files:**
- `Runtime/Unity/Decoders/DecoderRegistry.cs` (modify)
- `Runtime/Unity/link.xml` (modify)

Required behavior:

1. Route decode behavior by platform/capability policy:
   - Editor + standalone: threaded decode enabled above thresholds,
   - iOS/Android IL2CPP: enabled with stricter concurrency,
   - WebGL: disabled by default and fallback baseline path used.
2. If decoder is unsupported/unavailable, fallback deterministically to baseline decode path.
3. Emit one diagnostic warning per session for policy-disabled paths.
4. Preserve required decoder types for IL2CPP stripping behavior using `link.xml` as needed.
5. Baseline fallback must remain the existing Unity-native TurboHTTP path (`Texture2D.LoadImage` for images and current Unity audio decode/temp-file flow for audio).

Implementation constraints:

1. Capability probes must be explicit and deterministic.
2. Fallback behavior is mandatory and fully tested.
3. Keep linker preserve set minimal and verified.
4. Unknown platforms must default to safe fallback policy.

---

## Step 4: Add Decoder Equivalence and AOT Coverage

**File:** `Tests/Runtime/Unity/DecoderMatrixTests.cs` (new)

Required behavior:

1. Add integration tests per mapped format on Editor and IL2CPP targets.
2. Validate managed decoder output dimensions/sample metadata against fallback path for golden assets.
3. Validate fallback path behavior when decoders are disabled/unavailable.
4. Add stripping/AOT coverage to catch missing-method/runtime preserve regressions.

---

## Verification Criteria

1. Each supported managed format has a passing integration path and deterministic fallback path.
2. Decoder registry selection is explicit and policy-driven across platforms.
3. IL2CPP stripping/AOT runs show no runtime decoder resolution failures.
4. Threaded decode remains optional and never regresses baseline Unity decode correctness.
5. Dependency/license metadata for decoder packages is documented for release compliance.
