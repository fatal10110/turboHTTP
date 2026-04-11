# Phase 22c.4: Validation and Edge Cases

**Depends on:** 22c.3 (protocol routing complete)
**Assemblies:** `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files modified:** test files only (no production code changes unless edge case issues are discovered)

---

## Context

With 22c.1–22c.3 complete, the core HTTP/2-through-proxy mechanism is implemented. This sub-phase validates edge cases, lifecycle semantics, and cross-cutting correctness that unit tests in earlier sub-phases could not easily cover:

1. **Connection death chain** — when a tunneled HTTP/2 connection dies, the full disposal chain (TLS stream → tunnel stream → proxy socket) must fire correctly.
2. **GOAWAY through tunnel** — `Http2Connection`'s existing GOAWAY handling must work when the underlying stream is a tunnel (no special-casing needed, but must be verified).
3. **Concurrent tunnel establishment race** — between `GetIfExists` (returns null) and `GetOrCreateAsync` (acquires init lock), two concurrent requests may begin separate CONNECT handshakes. The init lock prevents two HTTP/2 connections; the second tunnel's TLS stream is discarded. This waste is acceptable but must be verified to not cause a leak.
4. **Proxy authentication header isolation** — 22c.3 must route a sanitized tunneled request
   (via `PrepareHttpsProxyTunnelRequest` or equivalent shared-content clone) on both the HTTP/1.1
   and HTTP/2 paths so `Proxy-Authorization` never reaches the origin.
5. **`HasHttp2Connection` proxy-aware overload** — diagnostic helper added in 22c.1; verify via tests.
6. **BouncyCastle ALPN through tunnel** — verify BouncyCastle correctly negotiates `h2` ALPN through a CONNECT tunnel (best effort in unit tests; physical device validation on 22c.4 IL2CPP pass).

---

## Step 1: Connection Death Chain Validation

When a tunneled HTTP/2 connection dies, disposal must propagate through the full stack:

```
Http2Connection.Dispose()
  → TLS stream (wrapping tunnel stream) .Dispose()
    → tunnel stream (wrapping proxy TCP stream) .Dispose()
      → proxy TCP socket .Dispose()
```

**Test approach:** Use a mock/spy stream that records `Dispose()` calls. Construct the chain:
- `MockProxyTcpStream` (innermost) → records dispose
- `MockTunnelStream : Stream` wrapping `MockProxyTcpStream` → records dispose
- `MockTlsStream : Stream` wrapping `MockTunnelStream` → records dispose
- Construct `Http2Connection` with `MockTlsStream`

Call `Http2Connection.Dispose()` and assert all three mock streams were disposed.

**Why this matters:** If any layer forgets to dispose the inner stream, the proxy TCP socket leaks. With `TransferOwnership()`, the proxy connection is no longer managed by the pool — the `Http2Connection` is the sole owner.

**Completion criteria:**
- [ ] `Http2Connection.Dispose()` triggers disposal of the TLS stream
- [ ] TLS stream disposal triggers disposal of the inner tunnel stream
- [ ] Inner stream disposal closes the proxy socket (verified via mock capture)
- [ ] No double-dispose: `Http2Connection.Dispose()` called twice does not throw or double-close

---

## Step 2: GOAWAY Through Tunnel

`Http2Connection`'s read loop handles `GOAWAY` frames by failing all pending streams and marking `IsAlive = false`. This behavior is transport-agnostic — it operates on the `Stream` abstraction regardless of whether the stream wraps a direct TCP socket or a proxy tunnel.

**Verify:**
1. Send a `GOAWAY` frame from the server side through the mock tunnel stream.
2. All pending streams on the `Http2Connection` are failed with an appropriate error.
3. `IsAlive` becomes false.
4. The next `GetIfExists` for the tunnel key returns null (existing `IsAlive` check).
5. A subsequent request falls through to establish a new tunnel.

**Test approach:** Reuse the existing `Http2ConnectionTests` patterns. Feed the `Http2Connection` a mock stream that can emit frame bytes on demand (GOAWAY byte sequence). Confirm the read loop reacts correctly.

**Completion criteria:**
- [ ] GOAWAY received through tunnel fails pending streams
- [ ] `IsAlive` false after GOAWAY
- [ ] Next `GetIfExists(origin, port, proxy, pport)` returns null
- [ ] Connection re-establishment flow works (new tunnel from scratch)

---

## Step 3: Concurrent Tunnel Establishment Race

Between `GetIfExists` (returns null) and `GetOrCreateAsync` (acquires init lock for the compound key), two concurrent requests may both observe `GetIfExists == null` and both proceed toward establishing a CONNECT tunnel. The `_initLocks` mechanism in `Http2ConnectionManager` ensures only one `Http2Connection` is created, but the second request's tunnel TLS stream is discarded by `GetOrCreateAsync`'s fast path.

**Acceptable outcome:** The discarded TLS stream is disposed. The discarded proxy lease is disposed (the second request releases its proxy connection back to the pool or disposes it). No leak.

**Test approach:**
1. Two concurrent tasks, each calling `DispatchViaProxyAsync` for the same origin through the same proxy.
2. Both pass `GetIfExists` simultaneously (use a barrier or `TaskCompletionSource` to synchronize).
3. Assert: exactly one `Http2Connection` is retained under the compound tunnel key.
4. Assert: if both requests race far enough to start CONNECT/TLS work, the discarded second
   tunnel stream/lease is disposed cleanly.
5. Assert: both requests complete successfully (the second reuses the retained connection).
6. Assert: no leaked streams or sockets (use mock streams with dispose-tracking).

**Completion criteria:**
- [ ] Concurrent tunnel establishment produces exactly one retained `Http2Connection` under the tunnel key
- [ ] Both requests complete without error
- [ ] No stream or socket leaks from the discarded second tunnel

---

## Step 4: Proxy Authentication Header Isolation

22c.3 must dispatch a sanitized tunneled request on both the HTTP/1.1 and HTTP/2 paths. The
existing `PrepareHttpsProxyTunnelRequest(...)` helper can be reused for that purpose, or the
implementation can prepare an equivalent shared-content clone before the HTTP/2 fast path.

Verify:

1. A request with `Proxy-Authorization` header sent through an HTTP/2 tunneled connection does not forward `Proxy-Authorization` to the origin server.
2. Other proxy-specific headers (if any) are similarly stripped.
3. The `CONNECT` phase (which is HTTP/1.1) correctly uses and then strips `Proxy-Authorization`.

**Test approach:**
- Mock HTTP/2 connection that captures the `UHttpRequest` passed to `DispatchAsync`.
- Assert the captured request does not contain `Proxy-Authorization`.
- Assert the CONNECT request (captured via mock proxy stream) does contain `Proxy-Authorization`.

**Completion criteria:**
- [ ] `Proxy-Authorization` not forwarded to origin on HTTP/2 path through tunnel
- [ ] `Proxy-Authorization` present in the CONNECT request to the proxy

---

## Step 5: `HasHttp2Connection` Proxy-Aware Overload

Added in 22c.1. Verify via tests:

- [ ] `HasConnection(origin, port, proxy, pport)` returns false before any tunnel established
- [ ] Returns true after `GetOrCreateAsync(origin, port, proxy, pport, ...)` completes
- [ ] Returns false after `Remove(origin, port, proxy, pport)` or after the connection dies
- [ ] Does not interfere with `HasConnection(origin, port)` (direct connection variant)

---

## Step 6: BouncyCastle ALPN Through Tunnel (Unit + IL2CPP)

**Unit test:** Use a mock `ITlsProvider` (BouncyCastle stand-in) that returns `NegotiatedAlpn = "h2"`. Verify the full flow:
1. SslStream provider throws `PlatformNotSupportedException` → `MarkSslStreamBroken()` called
2. BouncyCastle retry with fresh lease → mock BC provider returns `NegotiatedAlpn = "h2"`
3. `Http2ConnectionManager.GetOrCreateAsync(proxy overload)` called
4. Request dispatched over HTTP/2

**IL2CPP device test (manual, 22c.4 gate):** On a physical iOS or Android IL2CPP device:
1. Configure a test CONNECT proxy (can use a local proxy like Squid or Charles in a CI environment)
2. Connect with `TlsBackend.BouncyCastle` forced
3. Confirm ALPN `h2` is negotiated through the tunnel
4. Confirm HTTP/2 frames are exchanged
5. Record result in the implementation journal

**Completion criteria:**
- [ ] Unit test: BouncyCastle retry through tunnel produces HTTP/2 connection
- [ ] IL2CPP: BouncyCastle ALPN `h2` negotiated through CONNECT tunnel on physical device (iOS or Android)

---

## Step 7: RFC Compliance Matrix Verification

| RFC | Section | Claim | Verified By |
|-----|---------|-------|-------------|
| RFC 9110 § 9.3.6 | CONNECT method | CONNECT request format, 200 response handling, tunnel lifetime | Existing tests + 22c.3 integration test |
| RFC 9113 § 3.2–3.3 | HTTP/2 connection preface | Preface + SETTINGS exchange through tunnel | 22c.3 integration test (mock server) |
| RFC 7301 § 3 | ALPN | Both `h2` and `http/1.1` offered; server selection honored | 22c.2 unit tests |
| RFC 9113 § 9.1 | GOAWAY | GOAWAY through tunnel fails streams correctly | Step 2 above |

---

## Specialist Agent Reviews

Both reviews are mandatory before marking 22c.4 complete:

- **unity-infrastructure-architect** — Review connection lifecycle (Step 1), concurrent race (Step 3), proxy header isolation (Step 4), and IL2CPP disposal chain correctness.
- **unity-network-architect** — Review GOAWAY semantics (Step 2), ALPN through tunnel (Step 6), RFC compliance matrix (Step 7), and tunnel ownership transfer correctness.

---

## Overall 22c Completion Criteria

After 22c.4, the following must all be true:

- [ ] All 22c.1 completion criteria met (manager key extension + unit tests)
- [ ] All 22c.2 completion criteria met (tunnel ALPN extension + unit tests)
- [ ] All 22c.3 completion criteria met (protocol routing + unit tests + integration test)
- [ ] All 22c.4 edge-case validations complete
- [ ] Both specialist reviews passed for each sub-phase
- [ ] CLAUDE.md Development Status updated: Phase 22c → COMPLETE
- [ ] Implementation journal entry created: `Development/docs/implementation-journal/2026-04-phase22c-proxy-http2-alpn.md`

---

## Notes

- No production code changes are expected in 22c.4 unless edge-case testing reveals bugs. If bugs are found, fix them in the relevant file and re-run the appropriate sub-phase reviews.
- The concurrent race (Step 3) is an accepted design trade-off (noted in the overview's Deferred Items). If testing reveals the wasted CONNECT is problematic (e.g., proxy with strict connection limits), add a per-origin×proxy establishment lock. Document the decision in the implementation journal.
- IL2CPP device testing requires physical hardware. If not available during this session, record the gap in the journal and track it as a follow-up validation item.
