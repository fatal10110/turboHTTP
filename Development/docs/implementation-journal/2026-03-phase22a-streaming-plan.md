# 2026-03 Phase 22a Streaming Plan

## What Was Added

Added a new standalone technical phase plan for end-to-end request/response streaming:

- `Development/docs/phases/phase-22a-end-to-end-streaming.md`

Also corrected the previous placement mistake: the plan no longer lives under the `phase22/` subphase tree and is no longer linked from the Phase 22 overview as if it were part of 22.x.

## Purpose

Phase 22 established the interceptor/handler boundary, but the codebase still has buffered-body constraints in the request model, HTTP/1.1 parser, decompression path, file download path, and buffered public API. The new plan defines Phase 22a as a separate clean-break phase that makes upload and download streaming first-class without sacrificing the small buffered fast path.

## Decisions Captured In The Plan

1. **No backward compatibility:** the plan is intentionally a clean break. It replaces the ambiguous single buffered `SendAsync(...)` public path with explicit buffered and streaming entry points.
2. **Dual-path performance model:** the plan does not force all traffic through a streaming-only path. Small buffered request/response handling remains a first-class optimization target.
3. **Pull-based body transfer:** the plan replaces per-chunk push callbacks as the primary body contract with request/response body-source abstractions so backpressure is explicit and HTTP/2 flow control can track bytes actually consumed.
4. **Replayability is explicit:** retries and redirects depend on request-body replayability, not on hidden buffering.
5. **Bounded memory on large payloads:** the plan sets explicit initial targets for send buffers, receive buffers, and HTTP/2 per-stream buffering so large uploads/downloads stop scaling managed memory with payload size.

## Why This Shape Was Chosen

The existing Phase 22 design is the right dispatch/interceptor direction, but true streaming still needs a different body-transfer contract. The current synchronous `IHttpHandler.OnResponseData(...)` callback is efficient for buffered replay and metrics, but it is not a sufficient long-term primitive for high-performance streaming because it does not model backpressure naturally and it leaves HTTP/1.1 and HTTP/2 with different ownership/drain assumptions.

The new plan keeps the Phase 22 dispatch/interceptor architecture, but moves body transfer into explicit request/response body sources and treats the buffered public API as an adapter above the same substrate.

## Files Modified

| File | Change |
|------|--------|
| `Development/docs/phases/phase-22a-end-to-end-streaming.md` | New standalone detailed streaming plan |
| `Development/docs/phases/phase22/overview.md` | Removed the incorrect link that implied 22a was part of Phase 22 |

## Validation

Documentation-only change:

- no runtime code changed
- no tests run

## Deferred Until Implementation

1. The exact `IResponseBodySource` queue implementation for HTTP/2 (`ring buffer` vs `segmented queue`) is left open pending benchmarks.
2. Small-body thresholds are initial targets only and will need measurement on mobile and IL2CPP.
3. Request replayability behavior for seekable streams will need tight implementation rules to avoid fragile caller-managed reset assumptions.
