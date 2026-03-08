# Phase 24.4: 3D Model Handlers (glTF)

**Depends on:** Phase 5
**Estimated Effort:** 1 week

---

## Step 0: Define glTF Asset and Importer Abstractions

Required behavior:

1. Define `GltfAsset` descriptor containing bytes, detected format (`.gltf` vs `.glb`), and metadata.
2. Define optional importer abstraction (`IGltfImporter`) for typed integration.
3. Define handler API shape (`GetGltfAsync`, response extension equivalents).

Implementation constraints:

1. TurboHTTP handler scope is transport + handoff, not full parsing/rendering.
2. Abstractions must stay importer-agnostic.
3. Optional importer dependency must not force extra package requirements.

---

## Step 1: Implement Download and Format Detection

Required behavior:

1. Download model payload from HTTP response.
2. Detect format from content type/extension/signature where possible.
3. Populate `GltfAsset` with normalized metadata and payload ownership.

Implementation constraints:

1. Detection should be deterministic and resilient to missing headers.
2. Large payload handling should avoid unnecessary copying.
3. Error paths should distinguish transport failure vs invalid format metadata.

---

## Step 2: Implement Third-Party Import Integration Hooks

Required behavior:

1. Expose hook points for external importers (`GLTFUtility`, `UnityGLTF`, etc.).
2. Provide optional flow that calls `IGltfImporter.ImportAsync(...)` when configured.
3. Keep core handler usable without any importer dependency.

Implementation constraints:

1. No hard dependency on specific importer packages in core handler assembly.
2. Importer invocation must preserve cancellation and error propagation.
3. Unsupported importer scenarios must produce clear diagnostics.

---

## Step 3: Document Dependency and Platform Expectations

Required behavior:

1. Document that Unity has no built-in glTF importer.
2. Provide setup guidance for supported third-party importer integration.
3. Document platform caveats and expected fallback behavior.

Implementation constraints:

1. Documentation must prevent the impression of built-in full glTF support.
2. Sample flows should show both raw descriptor usage and importer-assisted usage.

---

## Verification Criteria

1. `GetGltfAsync` returns valid descriptor data for `.gltf` and `.glb` inputs.
2. Format detection works for normal and missing-header cases.
3. Optional importer integration path works when importer is supplied.
4. No parser dependency is required when only download/handoff is used.
5. Integration tests cover success, malformed payload, cancellation, and missing importer behavior.
