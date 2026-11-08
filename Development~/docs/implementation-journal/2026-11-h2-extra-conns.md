# 2026-11 HTTP/2 extra connections per origin and idle-reuse PING probe (#238)

Lane `h2-extra-conns` (wave w1110). Optional parts of #16 and #22 left out of `h2-conn-mgmt`.

## What

1. `Http2Options.MaxConnectionsPerOrigin` (default 1). `Http2ConnectionManager` keeps an immutable
   array of connections per key (direct `host:port`, tunnel `origin|via|proxy`), replaced by
   compare-and-swap. A request uses the first connection with a free stream slot
   (`Http2Connection.HasFreeStreamSlot`: no admission waiter and open + reserved streams below the
   peer's `SETTINGS_MAX_CONCURRENT_STREAMS`). When every connection is saturated and fewer than the
   maximum are open, `GetIfExists` returns null, so `RawSocketTransport` dials (step 4b) and
   `GetOrCreateAsync` publishes the new connection. At the maximum, the least-queued connection is
   returned and the request waits in its admission queue. With the default of 1 the selection is the
   old one: the first connection that accepts new streams.
2. `Http2Options.ReuseProbeIdleThreshold` (default `TimeSpan.Zero`, off). When set, the read loop
   records a monotonic `Stopwatch` timestamp per inbound frame (Interlocked, 64-bit safe), and
   `Http2Connection.DispatchAsync` sends one PING before a request uses a connection idle past the
   threshold. The probe waits up to `PingTimeout`. When it times out, the connection's streams fail and
   the connection is disposed (the existing liveness path), and the request fails unsent as a
   not-processed `Http2RetryableRequestException`. `RawSocketTransport` evicts the connection and
   replays the request on a fresh connection (RFC 9113 Section 8.7), so the request itself succeeds.

## Files

- `Runtime/Core/Http2Options.cs`: two options, with XML docs, `Clone` and `IsDefault`.
- `Runtime/Transport/Http2/Http2ConnectionManager.cs`: connection set per key, `SelectConnection`,
  `TryPublish` (cap), `TryRemoveExact` (instance-exact CAS removal), `Remove`/`Dispose` over sets,
  the per-key extra-dial claim (`GetIfExists(..., out ExtraDialClaim)`, `ReleaseExtraDial`,
  `ReleaseExtraDialAndFallBack`).
- `Runtime/Transport/RawSocketTransport.cs`: direct and tunnel paths take the claim, release it once
  the dial ended (and in a `finally`), and fall back to the saturated connection when the extra dial
  fails (`FallBackAfterFailedExtraDial`).
- `Runtime/Transport/Http2/Http2Connection.Admission.cs`: `HasFreeStreamSlot`.
- `Runtime/Transport/Http2/Http2Connection.Liveness.cs`: one shared probe in flight
  (`GetOrStartProbe`, `_activeProbe`), used by the keep-alive loop and the reuse probe;
  `ProbeIdleConnectionBeforeReuseAsync`; idle clock; `BackdateInboundActivityForTest`.
- `Runtime/Transport/Http2/Http2Connection.cs`, `Http2Connection.ReadLoop.cs`: option wiring, the
  dispatch hook and the activity timestamp.
- `Documentation~/Modules/Transport.md`: both options.
- Tests (new files): `Tests/Runtime/Transport/Http2/Http2ConnectionManagerExtraConnectionTests.cs`,
  `Http2ConnectionTests.IdleReuseProbe.cs`,
  `RawSocketTransportHttp2ConnectionManagementTests.IdleProbeAndExtraConnections.cs`. Existing
  `RawSocketTransportHttp2ConnectionManagementTests` became `partial`, and its `Harness` takes
  `Http2Options`. The `GetConnectionKeys` reflection helper in `Http2ConnectionManagerTests` reads
  the new value type.

## Decisions

- **RFC 9113 Section 9.1 vs the issue text.** The issue says the RFC allows another connection "when
  stream limits are reached". The RFC text says "Clients SHOULD NOT open more than one HTTP/2
  connection to a given host and port pair" and lists replacements (stream ids running out, rekeying,
  errors) as the exceptions. Following the RFC, the default stays 1, and values above 1 are an explicit
  opt-in that the XML doc describes as a deviation for servers with a low concurrent-stream limit.
- **Saturation signal.** "No free stream slot" (a new request would queue), read under
  `_streamCreateLock`, rather than "`QueuedAdmissionCount > 0`". With the latter, one request would
  wait on the full connection before the next opens a connection. No threshold option was added.
- **Where the extra connection opens.** The transport dials only when `GetIfExists` returns null, so
  it returns null when it is worth opening another connection. `HasConnection` still reports any
  connection that accepts streams.
- **Never above the max.** The init lock is dropped from `_initLocks` after each publish (an earlier
  decision against unbounded growth), so two creators can run at once. `TryPublish` enforces the cap.
  The loser disposes its new connection and returns an existing one.
- **One extra dial at a time (review round 1).** Without it, every request that saw saturation got
  null and dialed: a burst of 1000 requests against a peer limit of 100 cost about 900 TCP+TLS
  handshakes, all but one closed by `TryPublish`. A per-key claim (`ConcurrentDictionary.TryAdd`,
  instance-exact removal) lets one request dial; the others queue on the least-queued connection.
  The claim is released right after `GetOrCreateAsync` returns or throws, before an HTTP/1.1
  dispatch, and in the `finally` of both the direct and the tunnel path.
- **Failed extra dial (review round 1).** With one connection per origin the request would have
  queued, so a failed extra dial (refused by a per-client connection cap, failed handshake) that the
  caller did not cancel and that did not time out falls back to the least-queued connection instead of
  failing with a network error. The fallback uses a connection whose certificate was already
  validated, so it does not bypass TLS.
- **One PING in flight.** Before this change the keep-alive loop owned the single outstanding-payload
  slot. A reuse probe running concurrently would have overwritten it and caused a false timeout. Both
  now share `_activeProbe`. A burst of requests on an idle connection sends one PING, which also
  limits PING frequency (RFC 9113 Section 10.5).
- **Idle definition.** No inbound frame for the threshold, measured on a monotonic clock. Tests
  backdate the timestamp through an internal hook instead of waiting.
- **Non-replayable bodies.** A not-processed failure is not replayed for a non-replayable body
  (`ShouldRetryH2Request`, unchanged). After a failed probe such a request fails with a network error,
  the same way it does today after a GOAWAY refusal. The XML doc says so.

## Deferred

None.

## Verification

- `./Tools~/ci/check.sh`: see the PR "Local gates" section for the pushed head.
- Unity PlayMode (the runtime tests are PlayMode), scoped:
  `--filter 'Http2ConnectionManagerExtraConnectionTests|Http2ConnectionManagerTests|Http2ConnectionTests|RawSocketTransportHttp2ConnectionManagementTests|Http2Options'`,
  242/242 passed after review round 1. The two round-1 transport tests fail with the transport change
  reverted ("no further connection was dialed"; "Unexpected end of HTTP/2 stream").
- Not run: the full Unity suites (periodic host-editor run), IL2CPP players, devices. The change
  uses no reflection or codegen in runtime code. Its shared state goes through Interlocked,
  Volatile, `_streamCreateLock` or `ConcurrentDictionary`.
