# Phase 15.4: Unity Extension I/O Hardening (Canonical Paths + Atomic Writes)

**Depends on:** Phase 11
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Add Canonical Path Safety Layer

**Files:**
- `Runtime/Unity/PathSafety.cs` (new)
- `Runtime/Unity/UnityExtensions.cs` (modify)

Required behavior:

1. Centralize path normalization and validation in a single utility.
2. Canonicalize paths using `Path.GetFullPath` before policy checks.
3. Enforce root confinement (requested output must remain under allowed root).
4. Reject traversal and root-escape attempts deterministically.
5. Keep all path joins via `Path.Combine` and cross-platform safe separators.

Implementation constraints:

1. Validation must run before any file I/O side effects.
2. Error messages must be actionable but must not leak sensitive absolute paths unnecessarily.
3. Canonicalization behavior must remain consistent across Windows/macOS/Linux.
4. Policy defaults must remain secure-by-default while preserving existing helper ergonomics.

---

## Step 2: Add Atomic Write Strategy and Optional Integrity Checks

**Files:**
- `Runtime/Unity/UnityExtensions.cs` (modify)
- `Runtime/Unity/PathSafety.cs` (new)

Required behavior:

1. Implement atomic write flow (`.tmp` file write -> flush -> replace/move final).
2. Ensure interrupted writes never leave corrupted final output files.
3. Add optional checksum verification hook for download helper APIs.
4. Keep backwards compatibility by defaulting to safe behavior without requiring caller migration.

Implementation constraints:

1. Temp-write location must share target filesystem where possible to preserve atomic replace semantics.
2. Replace/move behavior must handle platform-specific file lock semantics gracefully and preserve atomicity guarantees on iOS/Android writable locations.
3. Cleanup of abandoned `.tmp` files must be deterministic on failure paths.
4. Integrity check failure must not promote invalid output to final path.
5. If same-filesystem atomic replace cannot be guaranteed, fallback path must use explicit safe-copy protocol (`copy + flush + fsync + final replace`) with deterministic warning telemetry.

---

## Step 3: Add Path Safety and Atomic I/O Tests

**File:** `Tests/Runtime/Unity/UnityPathSafetyTests.cs` (new)

Required behavior:

1. Validate traversal blocking across relative and encoded path attack patterns.
2. Validate root confinement for allowed output roots.
3. Validate interrupted write behavior does not corrupt prior good file.
4. Validate checksum mismatch failures do not publish bad files.
5. Validate non-atomic fallback path behavior on simulated cross-filesystem/mount scenarios.

---

## Verification Criteria

1. Traversal and root-escape attempts are blocked consistently on all supported platforms.
2. Atomic write path prevents partial-file corruption under cancellation/failure.
3. Existing extension helper APIs remain backward compatible with secure defaults.
4. Optional integrity checks provide deterministic pass/fail behavior.
