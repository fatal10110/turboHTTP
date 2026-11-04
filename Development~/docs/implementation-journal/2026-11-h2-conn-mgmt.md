# 2026-11 — HTTP/2 connection management (#15, #16, #17, #22)

Wave w1030 lane `h2-conn-mgmt`. Builds on h2-stream-lifecycle (stream close flags, read-loop leases,
`RemoveActiveStream` returning true for one caller) and h2-write-path (stream-id allocation and HEADERS
under one `_writeLock` hold; lock order `_writeLock` then `_streamCreateLock`).

## What changed

- **Retry marker.** New internal `Http2RetryableRequestException : UHttpException`
  (`RequestNotProcessed`). Not processed (RFC 9113 §8.7): RST_STREAM(REFUSED_STREAM), streams above a
  GOAWAY last-stream-id, a request refused before its stream opened (GOAWAY, closed, stream ids used
  up, queued when the connection stopped accepting). Connection lost: `FailAllStreams` (read-loop
  error, PING timeout, dispose) and any dispatch failure after `_cts` was cancelled.
  `Http2Connection.ClassifyDispatchFailure` drops the marker once `ResponseStarted` is true, and a
  handler fault is rethrown as the original exception, so neither is ever replayed.
  `Http2Stream.Fail` before the response started completes the dispatch with the marked exception
  instead of calling `OnResponseError`; RawSocketTransport replays or reports it, so the handler sees
  one outcome.
- **#15.** All six H2 dispatch sites in RawSocketTransport go through `DispatchOnH2ConnectionAsync`:
  after a failure the connection is evicted only if `!AcceptsNewStreams`, and only that instance
  (`Http2ConnectionManager.EvictIfUnusable`, by the connection's `ManagerKey`). The key-based
  `Remove` calls are gone from the transport. `DispatchCoreAsync` is now prologue + a bounded retry
  loop around `DispatchAttemptAsync` (the old try/catch body, unchanged): not processed is replayed
  for any method up to `MaxH2UnprocessedRetries` (3); connection lost only for idempotent methods and
  only on the first attempt; both need a replayable body; never after transport dispose.
- **#16.** `Http2Connection.Admission.cs`: a request reserves a slot before `_writeLock`
  (`AcquireStreamSlotAsync`). Free slots = peer `MAX_CONCURRENT_STREAMS` − `_activeStreams.Count` −
  `_pendingAdmissions`, evaluated under `_streamCreateLock`, so a SETTINGS decrease applies to new
  streams at once and open streams stay (§6.5.2). FIFO `LinkedList<TaskCompletionSource<bool>>`;
  waiters are admitted by `PumpAdmissionWaitersLocked` (stream removal, reservation release, SETTINGS)
  or refused (GOAWAY, `FailAllStreams`), or removed by their cancellation token. Every removal from
  `_activeStreams` goes through `RemoveActiveStream`/`TryRemoveActiveStream` → `OnActiveStreamRemoved`,
  which takes the lock only when `_admissionWaiterCount` (Interlocked, incremented before the slot
  check) is non-zero.
- **#17.** `IsAlive` (read loop running, `_cts` not cancelled) is split from `AcceptsNewStreams`
  (alive, no GOAWAY, stream ids left). `FailAllStreams` no longer sets `_goawayReceived`. GOAWAY is
  applied under `_streamCreateLock` (lower-only last-stream-id, error code kept), refuses queued
  requests, fails streams above the id as not processed, and keeps the rest running. The manager
  evicts a non-accepting connection by instance; a live one goes to `_draining` (disposed on manager
  Dispose, pruned when dead). The connection closes itself once draining with no active streams and
  no reservations (`TryCompleteDrain`: inline from the GOAWAY handler, on the thread pool from other
  removal paths because those may run inside a stream's own cleanup).
- **#22.** `Http2Connection.Liveness.cs`: public `Http2Options.PingInterval` (30 s, `Zero` disables)
  and `PingTimeout` (10 s). The loop sends a PING only while streams are open, with a unique non-zero
  8-byte payload (Interlocked counter); the ACK handler clears it by CAS. The deadline starts before
  the write (a peer that stopped reading can block the write). No ACK in time: `FailAllStreams` with a
  "presumed dead" network error, then `Dispose`. The stale "we don't send PINGs" ACK branch is gone.

## Review round 1

- PING liveness: the read loop counts inbound frames (Interlocked); a probe whose ACK misses the deadline
  but that saw any frame arrive after the PING returns `PeerActive` instead of failing the connection
  (an ACK queued behind DATA or our own upload on a slow link). Test
  `Liveness_AckDelayedButFramesArriving_ConnectionStaysAlive`.
- Admission: a slot reserved before a SETTINGS frame lowered MAX_CONCURRENT_STREAMS is re-checked under
  `_streamCreateLock` before the stream id is allocated; over the applied limit the request is refused
  unsent (`Http2RetryableRequestException`, not processed; the reservation is released). Test
  `Admission_LimitLoweredWhileReservedRequestWaitsForWriteLock_RefusedUnsent`.
- `ClassifyDispatchFailure` split into a static overload unit-tested per branch
  (`ClassifyDispatchFailure_MarksReplayOnlyBeforeResponseStart`); transport tests
  `FailureAfterResponseStart_*DuringUpload_IsNotReplayed` (connection lost / REFUSED_STREAM after HEADERS while
  the PUT body is blocked on flow control, spare connection never used, OnResponseStart once). Finding: in
  those paths `Http2Stream.Fail` aborts the upload first, so the dispatch completes and the failure surfaces
  through the body; the response-started branches of the classifier are defensive and are covered by the
  unit test.

## Files

Runtime: `Runtime/Core/Http2Options.cs`; `Runtime/Transport/RawSocketTransport.cs`;
`Runtime/Transport/Http2/{Http2Connection.cs, Http2Connection.Admission.cs (new),
Http2Connection.Liveness.cs (new), Http2Connection.Lifecycle.cs, Http2Connection.ReadLoop.cs,
Http2ConnectionManager.cs, Http2Stream.cs, Http2RetryableRequestException.cs (new)}`.
Tests: `Tests/Runtime/Transport/Http2/Http2ConnectionTests.ConnectionManagement.cs`,
`Tests/Runtime/Transport/Http2/RawSocketTransportHttp2ConnectionManagementTests.cs` (new).

## Decisions

- Idle connections no longer send PINGs (they did every 30 s). NAT keepalive for idle connections is
  dropped in favour of battery and §10.5; a request on a half-dead idle connection is detected by the
  first liveness cycle and replayed once when idempotent.
- A connection-lost replay now also covers a connection that was just created (before, only the
  reused fast path retried, and only for exceptions thrown inside dispatch, not for stream failures).
- REFUSED_STREAM and GOAWAY-unprocessed replays are capped at 3 per request.
- A non-replayable body is never replayed, even when not processed (conservative).

## Deferred

- #238 (v1.1): extra connections per origin when the admission queue saturates, and a PING probe
  before reusing a long-idle connection (both optional in #16/#22).

## Verification

- `./Tools~/ci/check.sh`: OK (invariants, format, shadow build with analyzers).
- Local NUnit harness outside Unity (scratch csproj over the shadow-built DLLs, compiling
  `Tests/Runtime/Transport/Http2/**`, the transport tunnel tests and support files): all Http2 tests
  pass, 5 repeated runs; the only failures are tunnel tests that need BouncyCastle or the Unity
  editor monitor (environmental, unrelated).
- The new tests use APIs this change adds, so they do not compile against the old runtime.
- NOT run: Unity EditMode/PlayMode (host editor, periodic), IL2CPP, devices (#99).
