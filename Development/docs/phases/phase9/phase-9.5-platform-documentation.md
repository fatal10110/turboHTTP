# Phase 9.5: Platform Notes and Troubleshooting

**Depends on:** Phase 9.4
**Assembly:** `Documentation~`
**Files:** 1 new

---

## Step 1: Publish `PlatformNotes.md`

**File:** `Documentation~/PlatformNotes.md`

Required behavior:

1. Document supported platforms and scripting backends.
2. Document platform-specific limitations and recommended defaults.
3. Document known TLS/ALPN risk areas and fallback behavior.
4. Include troubleshooting guidance for common platform failures.
5. Include platform test checklist and validated matrix snapshot.

Implementation constraints:

1. Only publish claims supported by executed matrix tests.
2. Distinguish "supported", "experimental", and "not supported" explicitly.
3. Keep iOS/Android manifest and policy guidance concrete.
4. Keep terminology consistent with runtime API names.

---

## Verification Criteria

1. Documentation matches the validated platform matrix from 9.4.
2. Troubleshooting steps map to known error classes and logs.
3. Unsupported targets (for example, WebGL raw-socket limitations) are explicit.
4. Release checklist links to this document before feature-complete sign-off.
