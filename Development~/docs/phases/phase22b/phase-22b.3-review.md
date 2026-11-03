# Phase 22b.3 Review — HTTP/1.1 Response Trailer Parsing

**Reviewers:** unity-infrastructure-architect, unity-network-architect

---

## Round 1 (2026-03-21)

**Verdict:** Both reviews pass. No blocking correctness bugs.

### Infrastructure Architect Findings

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | `TryAddResponseTrailer` throws `FormatException` on CRLF injection, violating `Try` prefix contract. Rename to `AddResponseTrailer`. | Medium | Fixed R2 |
| 2 | Inconsistent trailer reader method names: `ReadChunkTrailersAsync` (BodySource) vs `ReadChunkedTrailersAsync` (Parser). | Low | Fixed R2 |
| 3 | `ProhibitedRequestTrailers` copies response set — overly restrictive but safe. Refine when 22b.4 is implemented. | Informational | Deferred to 22b.4 |
| 4 | TCS behavioral asymmetry: non-chunked abort throws synchronously, chunked abort returns faulted `ValueTask`. Same result at `await` site — document as intentional. | Informational | Accepted |

Test gaps: abort-path TCS fault, streaming CRLF injection, `DrainThenGetTrailersAsync` path.

### Network Architect Findings

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | No `MaxTotalTrailerBytes` limit. Individual lines capped at 8192 but no cumulative cap — DoS vector. Recommend 32KB limit. | Moderate | Fixed R2 |
| 2 | `Content-Location` missing from prohibited trailer list. | Low | Fixed R2 |
| 3 | Request trailer prohibited set includes response-only headers (`Age`, `Vary`, etc.). Cosmetic — overly restrictive is safe. | Cosmetic | Deferred to 22b.4 |

Test gaps: fragmented-read trailer parsing, abort-during-read TCS fault, buffered proxy path with trailers.

---

## Round 2 (2026-03-22)

**Verdict:** Both reviews pass. All round 1 findings resolved. No new blocking issues.

### Fixes Verified

| # | Fix | Infra | Network |
|---|-----|-------|---------|
| 1 | Renamed `TryAddResponseTrailer` → `AddResponseTrailer` | PASS | PASS |
| 2 | Added `MaxTotalTrailerBytes` (32KB) in both parser paths | PASS | PASS |
| 3 | Added `Content-Location` to `ProhibitedResponseTrailers` | PASS | PASS |
| 4 | Unified method names to `ReadChunkedTrailersAsync` | PASS | PASS |
| 5 | Fixed `EmitParsedResponseAsync` pool-return regression (snapshot status/headers before return) | PASS | PASS |
| 6 | Added missing tests (fragmented reads, size overflow, streaming CRLF, abort-path TCS, proxy trailers) | PASS | PASS |

### Confirmed Correct (cumulative)

- TCS lifecycle complete: all error/abort/dispose paths resolve the TCS before `CloseBody()`
- Pool safety: `ParsedResponse.Reset()` nulls `Trailers`; `HttpHeaders` not pooled, no use-after-return
- `TaskCompletionSource<HttpHeaders>` safe on IL2CPP/AOT (reference-type generic, no reflection)
- Module boundaries respected: no new asmdef changes, no cross-module coupling
- Allocation profile clean: TCS only for chunked bodies, `HttpHeaders.Empty` reused on non-trailer paths
- `GetTrailersAsync` cannot hang: TCS always resolved
- RFC 9112 Section 7.1.2: trailers parsed only after terminal chunk
- RFC 9110 Section 6.6.2: prohibited list covers all mandatory categories
- tchar validation correct per RFC 9110 Section 5.6.2
- CRLF injection blocked by `ValidateHeaderValue`
- `MaxTotalTrailerBytes` (32KB) enforced in both buffered and streaming paths
- `EmitParsedResponseAsync` snapshots status/headers before pool return — no use-after-return

### Observations (non-blocking)

1. **Detached-body path hardcodes `HttpHeaders.Empty` for trailers** — correct today (detached bodies are never chunked), but latent trap if future `IResponseBodySource` changes detach semantics. Worth a comment.
2. **Parser tests use `Task.Run(...).GetAwaiter().GetResult()`** — older pattern; newer tests correctly use `AssertAsync.Run`. Not a correctness issue.
3. **`totalTrailerBytes` counts `string.Length` (characters) not wire bytes** — matches existing `MaxTotalHeaderBytes` accounting. For RFC-compliant servers (ASCII tchar names, VCHAR/obs-text values), character count equals byte count. Acceptable.

### Still Deferred

1. IL2CPP physical device validation with chunked trailer response.
2. Refine `ProhibitedRequestTrailers` to RFC 9110 §6.5.2 request-specific set when 22b.4 lands.
3. Trailer persistence in cache replay.
