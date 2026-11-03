# Phase 24.5: Test Suite & Documentation

**Depends on:** 20.1, 20.2, 20.3, 20.4
**Estimated Effort:** 1 week

---

## Step 0: Build Test Matrix and Coverage Targets

Required behavior:

1. Define per-feature test matrix for decompression and all handlers.
2. Define mandatory scenarios: success, malformed payload, cancellation, missing dependency/decoder.
3. Define platform matrix expectations for Editor, Standalone, iOS, Android, WebGL.

Implementation constraints:

1. Keep test naming and organization aligned with existing test conventions.
2. Explicitly separate fast tests vs integration/platform tests.

---

## Step 1: Implement Decompression Middleware Tests

Required behavior:

1. Add gzip round-trip tests.
2. Add brotli round-trip tests.
3. Add unknown encoding pass-through test.
4. Add middleware-disabled behavior test.
5. Add brotli-unavailable fallback test.

Implementation constraints:

1. Tests must validate headers and decoded payload correctness.
2. Fallback assertions must verify deterministic logging/error behavior.

---

## Step 2: Implement Unity Handler Integration Tests

Required behavior:

1. AssetBundle: success, invalid payload, cancellation, temp-file lifecycle.
2. Video: download mode, URL mode, cancellation, size guard.
3. glTF: descriptor success, malformed payload, missing importer flow.

Implementation constraints:

1. Tests touching Unity main-thread behavior must validate dispatcher usage.
2. Temp-file tests must verify cleanup behavior across normal and failure paths.

---

## Step 3: Write Platform Docs and Usage Samples

Required behavior:

1. Publish per-handler platform compatibility tables.
2. Publish sample code for decompression and each Unity handler.
3. Document unsupported combinations and recommended alternatives.

Implementation constraints:

1. Samples must reflect final public API signatures.
2. Unsupported cases must include practical fallback guidance.

---

## Step 4: Validate Cross-Feature Integration

Required behavior:

1. Validate decompression behavior with cache middleware interaction.
2. Validate all Unity handlers satisfy Phase 15 memory/concurrency guardrails.
3. Validate deterministic failure behavior when decoders/importers are unavailable.

Implementation constraints:

1. Regression suite must ensure no behavior drift in existing content helpers.
2. Integration checks should be CI-runnable with clear pass/fail gates.

---

## Verification Criteria

1. All new tests pass and are stable.
2. Decompression tests cover gzip, brotli, unknown encoding, disable toggle, and fallback.
3. Handler tests cover success, malformed input, cancellation, and dependency-missing scenarios.
4. Platform matrix and samples are published and consistent with runtime behavior.
5. Phase 15 guardrails are verified for all Unity handlers.
6. Cache/decompression interaction is validated and documented.
