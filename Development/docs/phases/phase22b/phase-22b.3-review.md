# Phase 22b.3 Review — HTTP/1.1 Response Trailer Parsing

**Date:** 2026-03-21
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Verdict:** Both reviews pass. No blocking correctness bugs.

---

## Infrastructure Architect

### Findings

| # | Finding | Severity | Blocking |
|---|---------|----------|----------|
| 1 | `TryAddResponseTrailer` throws `FormatException` on CRLF injection, violating `Try` prefix contract. Rename to `AddResponseTrailer`. | Medium | No |
| 2 | Inconsistent trailer reader method names: `ReadChunkTrailersAsync` (BodySource) vs `ReadChunkedTrailersAsync` (Parser). | Low | No |
| 3 | `ProhibitedRequestTrailers` copies response set — overly restrictive but safe. Refine when 22b.4 is implemented. | Informational | No |
| 4 | TCS behavioral asymmetry: non-chunked abort throws synchronously, chunked abort returns faulted `ValueTask`. Same result at `await` site — document as intentional. | Informational | No |

### Confirmed Correct

- TCS lifecycle complete: all error/abort/dispose paths resolve the TCS before `CloseBody()`.
- Pool safety: `ParsedResponse.Reset()` nulls `Trailers`; `HttpHeaders` is not pooled, no use-after-return.
- `TaskCompletionSource<HttpHeaders>` is safe on IL2CPP/AOT (reference-type generic, no reflection).
- Module boundaries respected: no new asmdef changes, no cross-module coupling.
- Allocation profile clean: TCS only for chunked bodies, `HttpHeaders.Empty` reused on non-trailer paths.
- `GetTrailersAsync` cannot hang: TCS is always resolved by either the read-success path or an exception/abort/dispose path.

### Test Gaps Identified

- No test for abort-path TCS fault on chunked source (`Abort()` followed by `GetTrailersAsync`).
- No test for CRLF injection in the streaming body source path (only the parser test covers this).
- No test for `DrainThenGetTrailersAsync` path (chunked body never read by consumer before `GetTrailersAsync`).

---

## Network Architect

### Findings

| # | Finding | Severity | Blocking |
|---|---------|----------|----------|
| 1 | No `MaxTotalTrailerBytes` limit. Individual lines capped at 8192 but no cumulative cap — DoS vector. Recommend 32KB limit in both parser paths. | Moderate | No |
| 2 | `Content-Location` missing from prohibited trailer list. Defensible per RFC but recommend adding for completeness. | Low | No |
| 3 | Request trailer prohibited set includes response-only headers (`Age`, `Vary`, etc.). Cosmetic — overly restrictive is safe. | Cosmetic | No |

### RFC Compliance Confirmed

- **RFC 9112 Section 7.1.2:** Trailers parsed only after terminal chunk in both buffered and streaming paths.
- **RFC 9110 Section 6.6.2:** Prohibited list covers all mandatory categories (framing, routing, request modifiers, authentication, control data, self-reference).
- **tchar validation:** `IsRfc9110TChar` correctly implements RFC 9110 Section 5.6.2 token character set.
- **CRLF injection:** `ValidateHeaderValue` blocks embedded `\r` and `\n` in trailer values. `ReadLineAsync` strips trailing `\r`, so bare `\r` within a value is the only surviving vector — correctly caught.
- **Line length:** `MaxHeaderLineLength` (8192) applied to trailer lines, consistent with header limits.
- **Security:** Prohibited trailers prevent framing attacks (`Transfer-Encoding`, `Content-Length`), credential injection (`Authorization`), and header smuggling via trailers. Malformed lines silently skipped.

### Test Gaps Identified

- No test for fragmented-read trailer parsing (trailer line spanning buffer boundaries).
- No test for abort-during-read TCS fault path.
- No test for buffered `RawSocketTransport` proxy path with trailers present.

---

## Recommended Actions

### Should fix (this slice)

1. **Rename `TryAddResponseTrailer` → `AddResponseTrailer`** — the method intentionally throws `FormatException` for CRLF injection (correct security behavior) but this violates the `Try` naming convention used consistently elsewhere in the parser.
2. **Add `MaxTotalTrailerBytes` (32KB)** to both `ReadChunkedTrailersAsync` implementations (parser and body source) — mirrors existing `MaxTotalHeaderBytes` pattern for headers.

### Track for follow-up

3. Add `Content-Location` to `ProhibitedResponseTrailers`.
4. Add missing test coverage: abort-path TCS fault, fragmented reads, streaming CRLF injection.
5. Refine `ProhibitedRequestTrailers` to RFC 9110 §6.5.2 request-specific set when 22b.4 lands.
6. IL2CPP physical device validation with chunked trailer response (already tracked in journal).
7. Unify method names: `ReadChunkTrailersAsync` / `ReadChunkedTrailersAsync`.
