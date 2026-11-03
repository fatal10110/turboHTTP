# Phase 22b.4 Review — HTTP/1.1 + HTTP/2 Request Trailers

**Reviewers:** unity-infrastructure-architect, unity-network-architect

---

## Round 1 (2026-03-22)

**Verdict:** All Round 1 findings addressed in the follow-up implementation pass on 2026-03-22.

### Infrastructure Architect Findings

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| B-1 | `_hasManagedTrailerHeader` is `bool`, not `int` — breaks ARM64 memory ordering for pooled request reuse. All other lifecycle fields use `Volatile`/`Interlocked` via `int`. | Blocking | Fixed |
| B-2 | `_trailerProvider` and `_declaredTrailerNames` in `UHttpRequestBody` are not volatile — ARM64 visibility hazard across send threads. | Blocking | Fixed |
| I-1 | Empty body + trailer provider silently dropped in HTTP/1.1 — `ResolveBodyWriteMode` returns `None` for empty buffered bodies, bypassing `ValidateTrailerCompatibility`. HTTP/2 handles correctly via `HasRequestBody`. | Important | Fixed |
| I-2 | `SyncManagedTrailerHeader` allocates via `string.Join` on every `WithHeader` call when trailers are active. Should only re-sync when `Trailer` header itself is touched. | Important | Fixed |
| I-3 | `PrepareRequestTrailers` allocates `List<(string,string)>` per request — could populate `_headerListScratch` directly inside `_writeLock`. | Important | Fixed |
| I-4 | `CopyTrailerConfigurationFrom` is `private` but called on external instance in `CloneDetached`. Should be `internal` for consistency with other assembly-internal infrastructure. | Important | Fixed |
| M-1 | `IsRfc9110TChar` duplicated 3x (Core, Http1, Http2). Consolidate in Transport.Internal; Core copy must remain (Core cannot reference Transport). | Minor | Fixed |
| M-2 | `ValidateDeclaredTrailerNames` doesn't reject prohibited names at config time (Core can't reference Transport). Document or defer. | Minor | Fixed |
| M-3 | Test `WithRequestTrailers_WithHeaders_ReappliesManagedTrailerDeclaration` should assert `GetValues("Trailer").Count == 1`. | Minor | Fixed |

### Network Architect Findings

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| B-3 | HTTP/2 path does not filter user `Trailer` header from initial HEADERS frame. HTTP/1.1 skips and regenerates from `DeclaredTrailerNames`; HTTP/2 lets managed header flow through generic loop. Fragile divergence — must explicitly skip and regenerate. | Blocking | Fixed |
| I-5 | Prohibited trailer set documentation — `ProhibitedRequestTrailers` copies response set (includes `Age`, `Vary`, `Location`, etc.). Overly restrictive but safe. Add code comment explaining conservative choice. Closes deferred 22b.3 item. | Important | Fixed |
| I-6 | `WithRequestTrailers` on `EmptyRequestBody` succeeds but fails at HTTP/1.1 serialization. Add doc comment that HTTP/1.1 requires streaming/chunked body for trailers. | Important | Fixed |
| M-4 | Timeout-based frame absence test (`TryReadFrameAsync(250ms)`) is timing-dependent on slow CI or IL2CPP. | Minor | Fixed |
| M-5 | `ValidateDeclaredTrailerNames` doesn't check for duplicate names in the declared list. | Minor | Fixed |

### Confirmed Correct

- Terminal chunk format `0\r\n` + trailers + `\r\n` per RFC 7230 §4.1.2
- HTTP/2 `END_STREAM` on trailing HEADERS, not last DATA frame — per RFC 7540 §8.1
- Trailer name lowercasing for HTTP/2 — per RFC 7540 §8.1.2
- CRLF injection protection on both HTTP/1.1 and HTTP/2 send paths
- `TE: trailers` not required for sending request trailers (only for accepting response trailers)
- `Func<HttpHeaders>` delegate has no IL2CPP/AOT concerns
- `HasRequestBody() = true` when `TrailerProvider != null` with empty body — correct for HTTP/2
- `Http2ErrorCode.Cancel` appropriate for client-side validation failures
- Undeclared trailer fields allowed per RFC 9110 §6.5.1 (SHOULD, not MUST)
- No-trailer hot path has zero extra allocations
- `ResetForPool()` correctly clears all trailer state via `ReplaceContent` + `SyncManagedTrailerHeader`
- `CopyTrailerConfigurationFrom` handles null source, empty names correctly
- `ValidateTrailerCompatibility` positioned before any bytes are written — no partial write to undo
- HTTP/2 known-length streaming path correctly places `END_STREAM` on trailing HEADERS when trailers present, on last DATA when trailers null/empty
- HTTP/2 empty body + trailers: no spurious DATA frame emitted, trailing HEADERS carries `END_STREAM`
- Module boundaries respected: no new asmdef changes, no cross-module coupling
- `FileRequestBody.CloneDetachedCore()` inherits trailer copy from base `CloneDetached()` wrapper

### Still Deferred

1. IL2CPP physical device validation (HTTP/2 trailing HEADERS send, trailer-provider delegates under IL2CPP).
2. Trailer persistence in cache replay (outside 22b.4 scope).
3. 22b.1 (`Expect: 100-continue`) and 22b.2 (proxy streaming) remain pending sub-phases.

---

## Round 1 Follow-up (2026-03-22)

All Round 1 fixes were applied and revalidated in focused local harnesses.

- Core/body + builder: `phase22b.4 step2 checks passed (17 tests)`
- HTTP/1.1 serializer: `phase22b.4 step4 checks passed (47 tests)`
- HTTP/2 send path: `phase22b.4 step5 checks passed (7 tests)`

---

## Round 2 (2026-03-23)

**Verdict:** Both reviews pass. All Round 1 findings verified as correctly resolved. No new blocking issues.

### Fixes Verified

| # | Fix | Infra | Network |
|---|-----|-------|---------|
| B-1 | `_hasManagedTrailerHeader` changed to `int` with `Volatile.Read`/`Interlocked.Exchange` at all access points | PASS | — |
| B-2 | `_trailerProvider`/`_declaredTrailerNames` use `Volatile.Read`/`Volatile.Write`; correct store ordering (names before provider) | PASS | — |
| B-3 | HTTP/2 `DispatchAsync` skips user `Trailer` header, regenerates from `DeclaredTrailerNames` via `AppendRequestTrailerDeclarationHeader` | — | PASS |
| I-1 | `ResolveBodyWriteMode` returns `Chunked` for empty body with `TrailerProvider` (both buffered-empty and zero-length paths) | PASS | PASS |
| I-2 | `WithHeader` only calls `SyncManagedTrailerHeader` when the header name is `"Trailer"` | PASS | — |
| I-3 | `PrepareRequestTrailerBlockAsync` uses `_headerListScratch` inside `_writeLock`; `SendTrailingHeadersAsync` accepts pre-encoded `ReadOnlyMemory<byte>`; no deadlock (lock fully released between prepare and send) | PASS | PASS |
| I-4 | `CopyTrailerConfigurationFrom` changed from `private` to `internal` | PASS | — |
| I-5 | `ProhibitedRequestTrailers` comment documents conservative design choice | — | PASS |
| I-6 | `WithRequestTrailers` doc comment mentions HTTP/1.1 chunked requirement and empty-body promotion | — | PASS |
| M-1 | `IsRfc9110TChar` consolidated to `EncodingHelper.IsRfc9110TChar`; Core keeps local copy | PASS | — |
| M-2 | Comment in `ValidateDeclaredTrailerNames` explains Transport-deferred prohibition check | PASS | — |
| M-3 | `GetValues("Trailer").Count == 1` assertion added to test | PASS | — |
| M-4 | Timeout-based frame absence test replaced with deterministic `END_STREAM` assertion on DATA frame | — | PASS |
| M-5 | `ValidateDeclaredTrailerNames` uses `HashSet<string>(OrdinalIgnoreCase)` for dedup; test covers case-insensitive duplicate rejection | — | PASS |

### Additional Protocol Checks (Round 2)

- `trailer` declaration header value preserves original casing in HTTP/2; lowercasing applied only to actual trailing HEADERS field names — correct per RFC 7540 §8.1.2
- HTTP/1.1 empty-body + trailers path confirmed end-to-end: `EmptyRequestBody.OpenReadSessionCoreAsync` → immediate 0-byte read → `0\r\n<trailers>\r\n` terminal block
- `_writeLock` acquisition is sequential (prepare → release → send → release), no nested lock risk
- `Volatile.Write` store ordering in `SetRequestTrailers`: declared names written before provider — concurrent reader seeing new provider will also see new names

### Observations (non-blocking)

1. `trailerProvider()` is invoked while holding `_writeLock` in `PrepareRequestTrailerBlockAsync`. Slow user-supplied providers would block the write lock longer than necessary. Acceptable given trailer providers are expected to be cheap synchronous lambdas.
2. `ValidateTrailerCompatibility` is now effectively a dead guard for the `KnownLength + trailers` combination, since `ResolveBodyWriteMode` already ensures trailers always produce `Chunked`. Not harmful — defense-in-depth.
3. HTTP/2 empty body + trailers sends HEADERS → trailing HEADERS (no DATA frame). Valid per RFC 9113 §8.1 but unusual. Worth documenting for server compatibility awareness.

### Still Deferred

1. IL2CPP physical device validation (HTTP/2 trailing HEADERS send, trailer-provider delegates under IL2CPP).
2. Trailer persistence in cache replay (outside 22b.4 scope).
3. 22b.1 (`Expect: 100-continue`) and 22b.2 (proxy streaming) remain pending sub-phases.
