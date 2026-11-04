# 2026-11: first-byte retry waits for the abandoned read before it opens a socket (#364, #359)

## What

- **#364.** After the first-byte watchdog (#47) fires on a reused connection, the transport now waits
  for the head read it abandoned (#346) to end before it throws the stale-connection `IOException`
  that starts the retry on a new socket. The wait is bounded by
  `RawSocketTransport.AbandonedReadSettleTimeout` (internal, 1 s) and by the request token.
- **#359.** `RawSocketTransport_StaleConnection_NonReplayableKnownLengthBody_NoRetry` waits until the
  server has counted the injected connection before it sends the request.

## Root cause (#364)

The iOS run 7 failure stack (`results.xml` of run 7, `TwoSilentIdleConnections_IdempotentGet_RetriesOnNewlyConnectedSocket`)
shows where the request waited when the 5 s request timeout fired: in
`ParseHeadAsync(Stream, ...)`, which is the *unarmed* head read.

- The first attempt is always armed. It dequeues an idle connection, so `IsReused` is true, and the
  GET is idempotent.
- The retry uses `GetNewConnectionAsync` (`skipIdle: true`), so it never takes the second idle socket.
- So the issue's hypothesis is wrong: the retry's own receive on the *newly connected* socket never
  completed.

Why the receive was lost (libil2cpp source, identical in Unity 2021.3.45f2 and 6000.3.26f1):

- `icalls/System/System.Net.Sockets/Socket.cpp:477-495`: `Close_icall` closes the descriptor without
  `threadpool_ms_io_remove_socket`. The Mono runtime's close does call it, so the Editor is not affected.
- The `unityaot` `System.dll` never calls `IOSelector.Remove`. `SafeSocketHandle.ReleaseHandle` calls
  only `Shutdown_icall` and then `Close_icall`. This was checked with a System.Reflection.Metadata scan of
  the method bodies.
- So the receive pending on the disposed socket stays in the I/O selector's state, which is keyed by
  the descriptor number, until `poll()` reports the descriptor.
- `mono/ThreadPool/threadpool-ms-io.cpp`:
  - A new socket that gets the same number first appends its jobs to the stale list (`UPDATE_ADD`).
  - `wait_callback` dispatches only the first `EVENT_IN` job, which is the stale one.
  - On `EVENT_ERR` it erases the whole list.
- `threadpool-ms-io-poll.cpp` maps HUP to `EVENT_ERR`. Darwin's `poll()` reports `POLLHUP` together
  with `POLLIN` when the peer sends FIN. The test server sends its 200 and closes, so the new receive
  is dropped and the request waits for its timeout. This is iOS-specific: Linux reports no `POLLHUP`
  for a half-close.

Interaction with #346: before #346 the first attempt awaited the read on the disposed socket. That
read ends only when the selector has dispatched and dropped it, so the retry socket was always created
after the stale state was gone (runs 3, 5 and 6 passed in 0.21 s). #346 abandons the read and the retry
connects at once, which opens the descriptor-reuse window. The window is narrow (one failure in four
iOS runs), and whether the retry hits it depends on which descriptor number the new socket gets.

## Files

- `Runtime/Transport/RawSocketTransport.cs`:
  - `AbandonedReadSettleTimeout` (internal).
  - `AwaitFirstResponseByteAsync` now takes the settle bound. The catch only records the failure; the
    wait and the throw happen after the try, so no `await` runs inside the filtered catch (#320).
  - `WaitForAbandonedReadAsync`.
- `Runtime/Transport/Http1/Http11ResponseParser.cs`: `BufferedStreamReader.AbandonedHeadRead`
  (the raced read, `_raceRead`).
- `Tests/Runtime/Transport/Http1/ReusedConnectionRetryInterleavingTests.cs` (new, 3 tests). The idle
  connections are real sockets to a silent listener behind a stream whose read ignores its token and
  `Dispose`, so the read is always abandoned by the race, as on IL2CPP.
- `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs`: #359 waits on a completion source that
  the server sets for connection 1. Handlers run after the accept count.

## Decisions

- **Wait for the read, not "arm the retry lease".** The lost receive is on the new socket, so a
  first-byte bound there would only turn the hang into a second failure. The issue's other option,
  "request a new socket explicitly", was already implemented (`GetNewConnectionAsync`).
- **Bounded at 1 s.** On IL2CPP the read ends within milliseconds: `ReleaseHandle` shuts the socket
  down, which wakes the selector. The bound only applies where the platform never ends the read (the
  intermittent Mono case in #293). There the retry is delayed by at most 1 s instead of hanging. The
  bound is a fixed internal value rather than the pool's first-byte timeout (10 s by default), which
  would be too long a delay.
- **Only the first-byte retry path.** Other abandoned reads, followed by any new socket, have the same
  IL2CPP window. That needs a general design and an iOS reproduction first, so it went to a follow-up
  issue (#369).
- **#359: only the failing test.** The sibling `AcceptCount == N` assertions are in #370.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Host editor PlayMode (Unity 2021.3.45f2, Mono), filter
  `ReusedConnectionFirstByteTimeoutTests|RawSocketTransport_StaleConnection|HeadReadCancellationTests|BodyReadCancellationTests|ReusedConnectionRetryInterleavingTests`:
  25/25 passed in 3 of 3 runs.
- Without the fix (the wait turned into a no-op), `ReusedConnectionRetryInterleavingTests`:
  - `AbandonedReadStillPending_RetryDoesNotOpenASocketBeforeTheSettleBound` fails deterministically:
    the retry opens a socket while the read is pending.
  - `AbandonedReadEndsAfterDispose_RetrySocketOpensOnlyAfterTheReadEnded` passed on Mono anyway. Its
    ordering race goes either way without the fix, and it is deterministic only with the fix.
  - `TwoIdleConnectionsWithStuckReads_...` passes without the fix: the retry already used a new socket
    on main.
- Android emulators: see the PR body (API 29 and API 33, IL2CPP High, same filter).

## Not run

- iOS simulator: it belongs to another session. #364 needs a repeated iOS re-run of
  `ReusedConnectionFirstByteTimeoutTests` and `ReusedConnectionRetryInterleavingTests`
  (needs-device-validation). The Editor cannot show the IL2CPP selector behaviour, and an Android pass
  says little, because Linux `poll()` reports no `POLLHUP` on a half-close.
- Full Unity suites: these run in the periodic host-editor run.
