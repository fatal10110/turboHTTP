# Phase 10.10: Streaming Transport Improvements

**Depends on:** Phase 9
**Assembly:** `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files:** 1 new, 2 modified

---

## Step 1: Optimize HTTP/1.1 Response Parsing

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modify)

Required behavior:

1. Remove byte-by-byte header reads from the hot path.
2. Parse status line and headers using buffered reads with incremental boundary detection.
3. Preserve existing correctness for:
   - `Content-Length` bodies;
   - chunked transfer bodies;
   - header folding/repeated-header semantics supported by current parser.
4. Preserve multiple `Set-Cookie` header values without lossy merging.
5. Keep cancellation and timeout handling behavior unchanged.

Implementation constraints:

1. Use bounded buffers and avoid unbounded concatenation growth.
2. Minimize per-request allocations and temporary arrays.
3. Keep parser state machine deterministic for partial reads and socket fragmentation.
4. Preserve existing error typing/messages where tests depend on them.

---

## Step 2: Add Regression and Performance Coverage

**Files:**
- `Tests/Runtime/Transport/Http11ResponseParserTests.cs` (modify)
- `Tests/Runtime/Transport/Http11ResponseParserPerformanceTests.cs` (new)

Required behavior:

1. Existing parser regression tests continue to pass with buffered path.
2. Add tests for fragmented header boundaries across multiple reads.
3. Add tests for large header blocks to validate bounded memory behavior.
4. Add performance guard test demonstrating reduced allocation/task churn versus baseline.
5. Add split-boundary tests where parser buffer boundaries land inside sensitive tokens (for example `\r\n`) and within multi-byte payload sequences.

---

## Verification Criteria

1. Functional parser behavior remains equivalent for existing protocol cases.
2. Header parsing no longer relies on one-byte read loops.
3. Performance guard confirms meaningful allocation reduction on representative payloads.
4. Streaming path remains stable under cancellation and truncated-stream scenarios.
5. Split-boundary cases (delimiter and multi-byte payload boundaries) remain correct under fragmented reads.
