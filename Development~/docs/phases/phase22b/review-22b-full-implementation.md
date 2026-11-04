# Phase 22b Full Implementation Review

**Date:** 2026-04-08 (first pass) / 2026-04-10 (second pass — all P2s cleared)
**Scope:** Complete Phase 22b (22b.1-22b.4) — Streaming Extensions
**Reviewer:** Infrastructure Architect + Network Architect (specialist agents)
**Review model:** Infrastructure and network rubrics applied as explicit checklists
**Review basis:** Full phase22b implementation including the 2026-04-07 follow-up fixes (P1/P2 from the previous Codex review pass are confirmed fixed)

---

## Second-Pass Verdict (2026-04-10)

All three P2 code/test findings from the first specialist pass are correctly fixed. No new issues were introduced. One P3 observation added. **Phase 22b is clear for sign-off** pending physical-device validation (P2-2).

| Fix | Status |
|---|---|
| P2-1: trailing HEADERS without END_STREAM — stream-local fail | **Pass** |
| P2-A: DrainProxyConnectBodyAsync chunked 407 drain | **Pass** |
| P2-B: Stopwatch timing assertion, 1000ms tolerance | **Pass** |
| P2-2: Concurrent SslStream/BouncyCastle physical-device test | **Open** (deferred, physical device required) |

New P3 from second pass:
- **P3-H:** `DrainChunkedBodyAsync` has no explicit total-bytes cap. Cancellation-token/transport-timeout is the effective bound, but an explicit cap (e.g., 1MB) would give cleaner failure semantics for a misbehaving proxy. Deferred to a future hardening pass.

---

---

## Review Summary

This review covered the complete Phase 22b implementation across:

- `22b.1` `Expect: 100-continue`
- `22b.2` proxy streaming and CONNECT tunnel reuse
- `22b.3` HTTP/1.1 response trailers
- `22b.4` request trailers

The two protocol issues identified by the prior Codex review (P1: non-100 1xx in expect-continue path; P2: HTTP/2 initial HEADERS pseudo-header validation) are confirmed fixed and regression-tested. Three new P2 issues were identified in this specialist review round.

---

## Infrastructure Review Findings

### PASS items

| Rubric | Result |
|---|---|
| Module boundaries | Pass — no cross-module refs; Transport sole unsafe assembly |
| IL2CPP/AOT safety | Pass — all TCS instantiations use concrete closed generics; `TaskCompletionSource<bool>` / `TaskCompletionSource<HttpHeaders>` are IL2CPP safe |
| Thread safety | Pass — `Volatile`/`Interlocked` used correctly; expect-continue loop structure is correct |
| Unity .NET Std 2.1 | Pass — no .NET 5+ APIs; `EncodingHelper.Latin1` fallback correct |

### P2-A: `DrainProxyConnectBodyAsync` does not drain chunked 407 bodies

**Severity:** P2 (should fix)

**File:** `Runtime/Transport/RawSocketTransport.cs`, lines 1021–1053

`DrainProxyConnectBodyAsync` only checks `Content-Length`. If a proxy returns `Transfer-Encoding: chunked` on a 407 response with credentials configured for a retry, the socket byte stream will be corrupt when the next CONNECT request is written on top of the unread body bytes. The proxy will see a malformed CONNECT and the retry fails with a misleading `NetworkError`.

This is limited in practice (most proxies use `Content-Length: 0` or no body on 407), but a conformant implementation must drain `Transfer-Encoding: chunked` bodies before reusing the socket.

**Required fix:** Add chunked body drain to `DrainProxyConnectBodyAsync`: read chunk-size lines (hex, with optional extensions) and data until the `0\r\n\r\n` terminal chunk. If `Transfer-Encoding` is present but not `chunked` (and no `Content-Length`), the safe option is to either read to connection close or not retry with the same socket (throw, forcing a new connection on re-entry).

### P2-B: `DateTime.UtcNow` timing assertion introduces flakiness risk

**Severity:** P2 (should fix)

**File:** `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`, lines 1706–1710

```csharp
var startedAt = DateTime.UtcNow;
await response.DisposeAsync();
var elapsed = DateTime.UtcNow - startedAt;
Assert.Less(elapsed, TimeSpan.FromMilliseconds(300));
```

`DateTime.UtcNow` resolution is ~15ms on some platforms. The 300ms gate is tight enough to flake under CI load.

**Required fix:** Replace with `System.Diagnostics.Stopwatch`. Increase tolerance to at least 1000ms to account for CI load, or tag the test `[Category("Performance")]` with an explicit flakiness note.

### P3 items (infrastructure)

- **P3-A:** Extract `new[] { "http/1.1" }` at `RawSocketTransport.cs` line 890 to a `private static readonly string[]` field — eliminates a per-CONNECT allocation.
- **P3-B:** Add a comment at `RawSocketTransport.cs` line 1359 explaining that `timeoutCts.Cancel()` is intentionally NOT called on `continue` for non-100 1xx — the expect-continue deadline must remain active for subsequent responses.
- **P3-C:** `ReadAsciiLineAsync` does byte-by-byte reads with two `ArrayPool<byte>` rentals per call. For the CONNECT cold path this is acceptable, but could be improved with a `BufferedStreamReader`-based approach in a future phase.
- **P3-D:** `Assert.AreNotSame(pendingLeaseTask, completed)` in `TcpConnectionPoolTests.cs` line 267 is technically correct but semantically opaque. `Assert.IsFalse(pendingLeaseTask.IsCompleted)` expresses intent more clearly.

---

## Network/Protocol Review Findings

### PASS items

| Rubric | Result |
|---|---|
| RFC 9110 §10.1.1 — Expect: 100-continue | Pass |
| RFC 9112 §7.1.2 — Chunked trailers | Pass |
| RFC 9110 §6.6.2 — Trailer field | Pass |
| RFC 9113 §8.1 — HTTP/2 100-continue | Pass |
| Proxy CONNECT — RFC 9110 §9.3.6 | Pass |
| Stale-retry correctness | Pass |
| Protocol error isolation — HTTP/2 | Pass |

### P2-1: HTTP/2 trailing HEADERS without END_STREAM silently accepted

**Severity:** P2 (should fix)

**File:** `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`, lines 375–385

In `DecodeAndSetHeaders`, when `isTrailingHeaders == true && endStream == false`, the code calls `stream.AppendTrailers(responseHeaders)` but does NOT call `stream.CompleteResponseBody()` or fail the stream.

Per RFC 9113 §8.1, trailing HEADERS MUST have `END_STREAM` set. A trailing HEADERS frame without `END_STREAM` is a protocol error — the stream will hang indefinitely waiting for more DATA or a DATA+END_STREAM that will never arrive, blocking the request until the connection timeout fires.

**Required fix:**

```csharp
if (isTrailingHeaders)
{
    // RFC 9113 §8.1: trailing HEADERS must carry END_STREAM.
    if (!endStream)
    {
        stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
            "Trailing HEADERS frame received without END_STREAM flag (RFC 9113 §8.1)")));
        RemoveActiveStream(stream.StreamId);
        return;
    }

    stream.AppendTrailers(responseHeaders);
    RemoveActiveStream(stream.StreamId);
    stream.State = stream.State == Http2StreamState.HalfClosedLocal
        ? Http2StreamState.Closed
        : Http2StreamState.HalfClosedRemote;
    stream.CompleteResponseBody();
}
```

**Related test gap:** A unit test that sends a trailing HEADERS frame without END_STREAM and asserts that the stream fails with a protocol error (rather than hanging). Should live in `Http2ConnectionTests.ProtocolAndCleanup.cs`.

### P2-2: Concurrent SslStream read+write on BouncyCastle and SAEA/PollSelect paths not validated

**Severity:** P2 (validation required, not a code fix)

**File:** `Runtime/Transport/RawSocketTransport.cs`, lines 1316–1471 (`SendOnLeaseWithExpectContinueAsync`)

The concurrent header-read + body-write pattern in the expect-continue path is safe on Mono/SslStream (documented concurrent read+write support). However, it has not been validated on:

1. **BouncyCastle TLS path (iOS IL2CPP):** `TlsProtocol.OfferInput/ReadOutput` is not documented as concurrent-read+write safe. If the BouncyCastle wrapper (`TlsStreamWrapper`) does not serialize reads and writes, data corruption is possible on iOS IL2CPP when the BouncyCastle backend is active.

2. **SAEA/PollSelect socket channels:** `SaeaStream` and `PollSelectStream` support simultaneous reads and writes at the channel level via independent `_receiveInFlight` and `_sendInFlight` tracking, but this has not been exercised under the expect-continue concurrent path in a physical-device test.

**Required action (before Phase 22b sign-off):** Physical-device validation on iOS IL2CPP with a server that delays the 100 response by 200ms (triggering the timeout-body-send concurrent path), testing under both `TlsBackend.SslStream` and `TlsBackend.BouncyCastle` backends. A large body (>1MB) should be used to make the concurrent window wide enough to exercise the race.

### P3 items (network/protocol)

- **P3-E:** `TrailerFieldValidator.ProhibitedRequestTrailers` is significantly more conservative than RFC 9110 §6.6.2 requires. Fields like `Digest`, `Content-MD5`, or custom integrity trailers are blocked. The comment acknowledges this is intentional deferred scope — should be revisited before v1 release.
- **P3-F:** `ReadChunkedTrailersAsync` silently discards invalid trailer lines (returns `false` from `AddResponseTrailer` without throwing). The permissive approach improves interoperability but diverges from strict RFC 9112 §7.1.3 handling. Should be documented explicitly.
- **P3-G:** `ct.ThrowIfCancellationRequested()` at `RawSocketTransport.cs` line 1404 checks the outer transport token (not the expired `timeoutCts.Token`). Correct behavior, but a comment clarifying that this is `ct` (request-scoped) would prevent future confusion.

---

## Validation Status

- `git diff --check 91f7b28..HEAD -- Runtime Tests/Runtime` — clean
- Local harness validation (64/64 passed) confirmed fixes for the prior Codex P1/P2 findings
- Unity temp-project PlayMode: 1210/1215 passed, 0 failed, 5 skipped (2026-04-07)
- Unity temp-project EditMode: 4/5 passed, 0 failed, 1 skipped (2026-04-07)

---

## Residual Risks (open, pre-existing)

1. Physical-device TLS / IL2CPP validation for Phase 22b networking paths:
   - iOS IL2CPP — especially BouncyCastle concurrent read+write (P2-2 above)
   - Android IL2CPP
2. Allocation-gate verification for non-`Expect: 100-continue`, non-trailer fast paths after 22b changes.

---

## Verdict

**Phase 22b is architecturally sound but has three P2 findings that should be addressed before closing the phase:**

1. **P2-1 (code fix):** HTTP/2 trailing HEADERS without END_STREAM — add guard in `DecodeAndSetHeaders`
2. **P2-A (code fix):** `DrainProxyConnectBodyAsync` — add chunked body drain for 407 auth retry
3. **P2-B (test fix):** Replace `DateTime.UtcNow` with `Stopwatch` in timing assertion
4. **P2-2 (validation):** Concurrent SslStream read+write on BouncyCastle/SAEA — physical device test required

P3 items are deferred to a follow-up pass or noted as future phase work.
