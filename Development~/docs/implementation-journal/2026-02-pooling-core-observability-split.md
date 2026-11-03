# Pooling Infrastructure Migration — Core / Observability Split

**Date:** 2026-02-27
**Status:** Complete

## What Was Implemented

Refactored module ownership for pooling and diagnostics:

1. Moved pooling infrastructure from `TurboHTTP.Performance` into `TurboHTTP.Core.Internal`.
2. Moved pool diagnostics/reporting runtime types from `TurboHTTP.Performance` into `TurboHTTP.Observability`.
3. Rewired transport and tests to consume Core-owned pooling types.
4. Removed `TurboHTTP.Transport` dependency on `TurboHTTP.Performance` for pooling internals.

## Files Moved

### To Core (`Runtime/Core/Internal`)
- `ObjectPool.cs`
- `ByteArrayPool.cs`
- `ArrayPoolMemoryOwner.cs`
- `PooledArrayBufferWriter.cs`
- `PooledSegment.cs`
- `SegmentedBuffer.cs`
- `SegmentedReadStream.cs`
- `HeaderParseScratchPool.cs`

### To Observability (`Runtime/Observability`)
- `PoolHealthReporter.cs`
- `PooledBuffer.cs`
- `link.xml` (StackTrace/StackFrame preservation for diagnostics builds)

## Integration Changes

- Updated transport imports to Core pooling namespace:
  - `Runtime/Transport/RawSocketTransport.cs`
  - `Runtime/Transport/Http1/Http11ResponseParser.cs`
  - `Runtime/Transport/Http1/ParsedResponsePool.cs`
  - `Runtime/Transport/Http2/Http2StreamPool.cs`
- Updated runtime tests using pool primitives:
  - `Tests/Runtime/Performance/ObjectPoolTests.cs`
  - `Tests/Runtime/Performance/StressTests.cs`
- Updated transport asmdef:
  - `Runtime/Transport/TurboHTTP.Transport.asmdef` now references `TurboHTTP.Core` only.

## Documentation Updates

- Updated architecture notes in `CLAUDE.md` to reflect:
  - pooling infra in Core
  - diagnostics/reporting in Observability
- Updated phase file path reference:
  - `Development/docs/phases/phase19a/phase-19a.0-safety-infrastructure.md`

## Notes

- Concurrency features were later moved into `TurboHTTP.RateLimit` (`Runtime/RateLimit`).
- Core retains lightweight pool counters (`ByteArrayPoolDiagnostics`, `ObjectPoolDiagnostics`) as data sources; reporting orchestration now lives in Observability.
