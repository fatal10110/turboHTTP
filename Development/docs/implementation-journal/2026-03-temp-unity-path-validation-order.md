# Temp Unity Path Validation Order Fix

**Date:** 2026-03-15  
**Scope:** Fix `UnityExtensions` path-validation ordering so traversal failures are reported before Unity main-thread-only data-path access.

## What Was Implemented

This pass fixes the PlayMode/runtime ordering bug behind `UnityExtensionsTests.DownloadToPersistentDataAsync_RejectsPathTraversal`.

`UnityExtensions.DownloadToPersistentDataAsync(...)` and `DownloadToTempCacheAsync(...)` previously touched `Application.persistentDataPath` / `Application.temporaryCachePath` before validating `relativePath`. In PlayMode paths that execute off the Unity main thread, a malicious relative path such as `../escape.bin` could fail with `UnityException` before the traversal guard ran.

The fix was:

1. Extract the root-independent relative-path checks from `PathSafety.ResolvePathWithinRoot(...)` into a new `PathSafety.ValidateRelativePath(...)` helper.
2. Call `PathSafety.ValidateRelativePath(...)` at the start of all four Unity download entry points:
   - `DownloadToPersistentDataAsync(...)`
   - `DownloadToPersistentDataAsync(..., UnityAtomicWriteOptions, ...)`
   - `DownloadToTempCacheAsync(...)`
   - `DownloadToTempCacheAsync(..., UnityAtomicWriteOptions, ...)`
3. Keep `PathSafety.ResolvePathWithinRoot(...)` in the final shared download path so canonical root confinement still runs after the actual Unity data path is resolved.

## Files Modified

| File | Change |
|------|--------|
| `Runtime/Unity/PathSafety.cs` | Added `ValidateRelativePath(...)` and reused it inside `ResolvePathWithinRoot(...)`. |
| `Runtime/Unity/UnityExtensions.cs` | Validates `relativePath` before reading Unity data-path properties in both persistent/temp-cache entry points. |
| `Tests/Runtime/Unity/UnityExtensionsTests.cs` | Kept the persistent-data traversal regression and added matching temp-cache coverage. |
| `Tests/Runtime/Unity/UnityPathSafetyTests.cs` | Added direct unit coverage for `ValidateRelativePath(...)`. |

## Assembly Boundary Check

Reviewed:

- `Runtime/Unity/TurboHTTP.Unity.asmdef`
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`

No asmdef changes were required. The fix stays inside `TurboHTTP.Unity` plus existing runtime tests.

## Decisions / Trade-Offs

1. **Validation is intentionally duplicated at the public entry points and in final path resolution:** the early call prevents Unity main-thread exceptions from masking invalid-path errors, while `ResolvePathWithinRoot(...)` remains the canonical last-mile confinement check.
2. **Public helper added intentionally:** `PathSafety.ValidateRelativePath(...)` is now the explicit root-independent validation surface for Unity path consumers in this module.
3. **No behavior change for valid calls:** successful downloads still resolve against Unity’s actual persistent/cache roots and still flow through the same atomic-write pipeline.

## Specialist Review Re-Run

Applied both required rubrics explicitly to this change:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Review Notes

- No dependency-boundary issues were introduced.
- The change is platform-safe for IL2CPP/AOT because it only reorders string/path validation and does not add reflection, generics-heavy code, or threading primitives.
- The fix reduces Unity main-thread sensitivity rather than expanding it.

## Validation

- `git diff --check` passes for the modified Unity files.
- Added regression coverage for:
  - persistent-data traversal rejection before Unity path access
  - temp-cache traversal rejection before Unity path access
  - direct `PathSafety.ValidateRelativePath(...)` traversal and encoded-traversal rejection

## Remaining Work

- Re-run the Unity PlayMode suite in the temp Unity project workflow to confirm the original failing test now reports `ArgumentException` consistently.
- IL2CPP/mobile validation remains pending for the broader Unity runtime surface outside this small ordering fix.
