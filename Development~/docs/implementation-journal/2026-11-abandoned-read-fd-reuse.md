# 2026-11: no new socket while an abandoned socket operation is pending (#369)

## What

- **#369.** Every socket operation abandoned on cancellation while still pending is recorded in a
  process-wide counter (`AbandonedSocketIo`, Core). `HappyEyeballsConnector.ConnectEndpointAsync`,
  the only place in `Runtime/` that creates a socket, waits before `new Socket(...)` until no
  recorded operation is pending. The wait is bounded: each entry expires after
  `AbandonedSocketIo.SettleTimeout` (internal, 1 s), and the wait honors the connect deadline and the
  caller token.
- This generalizes the #364 fix (PR #372). That fix waited only for the first-byte retry's abandoned
  head read. Its per-site wait (`RawSocketTransport.AbandonedReadSettleTimeout`,
  `WaitForAbandonedReadAsync`, `BufferedStreamReader.AbandonedHeadRead`) is folded into the gate.

## Root cause (recap; evidence from libil2cpp 2021.3.45f2)

- `icalls/System/System.Net.Sockets/Socket.cpp:477-495`: `Close_icall` releases and closes the
  descriptor. It never calls `threadpool_ms_io_remove_socket`. `:1327-1337`: `Shutdown_icall` only
  calls `socketHandle->Shutdown(how)`.
- `mono/ThreadPool/threadpool-ms-io.cpp`:
  - `UPDATE_ADD` (`:375` ff.) appends a job to the existing list of the descriptor number.
  - `wait_callback` (`:278-345`) takes the first job for the event through `get_job_for_event`
    (`:123-145`, which erases that job from the list). On `EVENT_ERR` it erases the whole list.
- `threadpool-ms-io-poll.cpp:203-208`: `POLLERR`, `POLLHUP` and `POLLNVAL` set `EVENT_ERR` (and
  `EVENT_IN`/`EVENT_OUT`).
- So a stale job leaves the list only when the selector dispatches it. The managed operation ends
  in the job's callback, so **the abandoned operation's end proves its job is gone**.

## Decisions

- **Option (a), a bounded gate before socket creation, not (b), `Shutdown(Both)` before close.**
  `SafeSocketHandle.ReleaseHandle` already runs `Shutdown_icall` and then `Close_icall` (journal
  `2026-11-reused-conn-retry.md`). An extra explicit shutdown wakes the selector the same way. It
  does not order the selector's dispatch before the descriptor is reused, because the selector
  thread runs asynchronously behind `updates_lock` and its wakeup pipe. The issue's other variant,
  "close only in the abandoned read's continuation", would move socket ownership at every dispose
  site, including `RegisterDisposeOnCancel`, `PooledConnection`, the WebSocket transport and the
  BouncyCastle glue. The gate is one place.
- **Process-wide, not per pool.** The selector state is global, keyed by descriptor number. A
  per-pool record would miss WebSocket sockets and reuse across transports.
- **Recorded at the abandon funnel.**
  - The cancel-wins branch of `AbandonOnCancel.WaitAsync` (both overloads) covers:
    - head reads (#346)
    - `ConnectReplyReader.ReadAsync`/`DrainBodyAsync` (CONNECT reply and the CONNECT 407 drain, for
      the HTTP transport and the WebSocket tunnel)
    - the WebSocket upgrade and line reads (#226/#357)
    - the BouncyCastle handshake (#344/#354)
    - `SslStream` authentication
    - the socket connect itself
  - `Observe(Task)` alone (the test helper `BoundedShutdown`) records nothing.
  - Body reads (#345) have no task. `AbandonBodyRead` calls `Begin()` and stores the entry with an
    Interlocked CAS. The read's completion swaps in `Entry.Ended` and ends whichever entry it finds.
    Whichever of the two runs second ends the entry, so the entry ends exactly once whatever the
    order. This covers the 407 proxy-challenge drain (`TryDrainProxyChallengeBodyAsync`) too.
- **Connects are recorded too.**
  - A stale connect (`EVENT_OUT`) job does not take a receive's dispatch.
  - With `EVENT_ERR`, though (a refused connect on the new socket), it still erases the new
    socket's job, so a refused connect would wait for its deadline instead of failing fast.
  - A Happy Eyeballs loser ends at the selector's next wakeup, at the latest when the winner's first
    receive registers. The gate only runs before the *next* socket.
- **Gate placement:** per attempt in `ConnectEndpointAsync`, with the attempt token (connect
  deadline + race + caller). It never stalls a race that is already running, and connector seams in
  tests are unaffected.
- **Bound 1 s per entry, waiter bound 1 s.** On IL2CPP the operation ends within milliseconds of the
  dispose (the shutdown wakes the selector). The bound only applies where the platform never ends
  the read (#293). An entry that never ends stops counting after the bound (a `Timer`, disposed when
  the operation ends first).
- **Fast path:** `WaitForSettledAsync` is one `Volatile.Read` and returns `Task.CompletedTask`.
  `WaitAsync`'s early-outs and the non-abandoned body read path are unchanged. Allocation happens
  only on an abandon: `Entry`, `Timer`, continuation.
- **Expect-100 `WaitForAbortedReadAsync` (2 s) left as is.** That request flow already waits for its
  own aborted head read before it continues (and it decides buffer pooling). If that read was raced
  and abandoned, it is recorded anyway.

## Files

- `Runtime/Core/Internal/AbandonedSocketIo.cs` (new) + `.meta`.
- `Runtime/Core/Internal/AbandonOnCancel.cs`: `Abandon` = `Track` + `Observe` on the cancel-wins branch.
- `Runtime/Transport/Connection/HappyEyeballsConnector.cs`: the gate before `new Socket`.
- `Runtime/Transport/Http1/Http11ResponseParser.cs`: body-read entry (`_abandonedBodyRead`).
  `AbandonedHeadRead` was removed.
- `Runtime/Transport/RawSocketTransport.cs`: the #372 per-site wait was removed.
- `Tests/Runtime/Transport/AbandonedSocketIoTests.cs` (new, 11 tests):
  - tracker unit tests
  - one ordering test per path (head, body, 407 drain, CONNECT reply, WebSocket upgrade,
    BouncyCastle handshake) through the real `HappyEyeballsConnector.ConnectAsync` to a loopback
    listener
- `Tests/Runtime/Transport/Http1/ReusedConnectionRetryInterleavingTests.cs`: sets the process-wide
  bound, restores it in `TearDown` and ends stuck reads there.

## Deferred

- #375 (v1.0-beta): sockets closed under an *awaited* pending read (the HTTP/2 read loop, the
  WebSocket receive loop on abort) are not abandons, so the gate does not see them.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Host editor PlayMode (Unity 2021.3.45f2, Mono), filter
  `AbandonedSocketIoTests|AbandonableTaskTests|HeadReadCancellationTests|BodyReadCancellationTests|ConnectReplyReaderTests|RawSocketTransportProxy407Tests|ReusedConnectionFirstByteTimeoutTests|ReusedConnectionRetryInterleavingTests|RawSocketTransport_StaleConnection|WebSocketHandshakeReadCancellationTests|WebSocketConnectCancellationTests|ProxyTunnelConnectorTests|BouncyCastleHandshakeAbandonTests|HappyEyeballs`:
  128/128 passed in each of 3 runs.
- Without the fix (filter `AbandonedSocketIoTests|ReusedConnectionRetryInterleavingTests`):
  - Recording off and gate off: 10 of 14 tests fail. Every per-path test fails with "the abandoned
    read must be recorded", and so does
    `AbandonedReadStillPending_RetryDoesNotOpenASocketBeforeTheSettleBound`.
  - Recording on and gate off: 7 of 14 fail. Every per-path test fails with "a socket was created
    while the abandoned read was pending", plus the #372 test.
- Android emulators: API 29 and API 33 (IL2CPP High, x86_64, same filter); see the PR body.

## Not run

- iOS simulator and device: these belong to another session. #369's first criterion (the
  reproduction attempt) and the iOS run stay needs-device-validation. The Editor (Mono) cannot show
  the loss, because Mono removes the job on close. Android says little, because Linux `poll()`
  reports no `POLLHUP` on a half-close.
- Full Unity suites: these run in the periodic host-editor run.
