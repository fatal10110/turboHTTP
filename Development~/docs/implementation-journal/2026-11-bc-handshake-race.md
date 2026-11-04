# 2026-11 — BouncyCastle handshake races its deadline and the caller token (#344)

## What

`BouncyCastleTlsProvider.WrapAsync` enforced the handshake deadline (#142) and caller cancellation by
disposing the inner stream and then awaiting the handshake task, which assumed that the pending
`ReadAsync` ends once its stream is disposed. On IL2CPP (iOS simulator run 5) it intermittently does
not: `ServerTricklingRecords_FailsAtTheHandshakeBound` (bound 1 s) and two
`WebSocketBouncyCastleTlsTests` cases ran for minutes. `WrapAsync` now races the handshake against a
token linked from the caller token and the deadline (`AbandonableTask.WaitAsync<T>`, #293/#346) and
returns as soon as either fires, whether or not the read ever ends.

## Files

- `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs`: `WrapAsync` awaits
  `AbandonableTask.WaitAsync(handshake, abandon.Token, innerStream)` where `abandon` is
  `CreateLinkedTokenSource(ct, deadline.Token)`. The existing single catch clause classifies the
  outcome (caller token first: `OperationCanceledException`; else `IOException` with an inner
  `TimeoutException`) and now also disposes the inner stream itself, because the abandon can win the
  race against the `DisposeStream` registration and the `finally` may unregister that callback
  before it ran. Remarks updated.
- `Tests/Runtime/Transport/Tls/BouncyCastleHandshakeAbandonTests.cs` (new): an inner stream whose
  first read ignores its token and `Dispose`. Deadline (300 ms bound): `IOException(TimeoutException)`
  within the bound, stream disposed, the read still pending, one read and one ClientHello. Caller
  cancel: `OperationCanceledException`, read still pending, stream disposed. A late read fault after
  the abandon is observed (`UnobservedTaskProbe`; skips through `IgnoreEnvironment` only on a runtime
  that reports no unobserved task at all, like the existing SslStream probe test, so no new counted
  skip on the host editor).

## Decisions

- **Linked token, one catch clause.** `AbandonableTask` stays unchanged (lane cancel-reads may edit
  it); the deadline/caller distinction keeps the `ct.IsCancellationRequested`-first rule inside the
  one filtered catch clause (#320).
- **Abandoned resource = inner stream.** The observer disposes the inner stream when the abandoned
  handshake ends. A handshake that succeeds after the abandon leaves a `BouncyCastleTlsStream` that is
  never handed out; it holds no pooled buffers (`_readBuffer`/`_writeBuffer` are plain arrays) and
  disposing it would try a close_notify write on a dead transport, so only the inner stream is
  disposed.
- **One handshake per stream.** Nothing is retried on the same stream; the caller (connection pool,
  CONNECT tunnel, WebSocket transport) disposes it and fails, as before.
- **SslStream provider: no change.** `SslStreamTlsProvider.WrapAsync` already races
  `AuthenticateAsClientAsync` against `ct` and observes the abandoned task (#172,
  `SslStreamSingleHandshakeTests.WrapAsync_CancelledPlainHandshakeFaultsLater_FaultIsObserved`). It has
  no separate handshake deadline (only the request timeout through `ct`); that is a design difference,
  not this defect.

## Deferred

- #344 acceptance criterion 4 (the iOS simulator and device runs of
  `ServerTricklingRecords_FailsAtTheHandshakeBound` and the two `WebSocketBouncyCastleTlsTests` cases)
  needs device validation (#99); the PR is `Part of #344`.
- `phase3c/step-3c.7-bouncycastle-provider.md` still describes the pre-#142 30 s `CancelAfter`; left
  as history.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity: the host editor was unreachable from WSL (Windows interop `UtilAcceptVsock` timeout), so no
  Unity EditMode/PlayMode, no IL2CPP emulator run, no iOS, no device. Instead the shadow-built test
  assembly ran under .NET 8 with NUnit 3.14 through a scratch reflection runner (outside the repo):
  the new fixture fails all three tests on the old provider (15 s guard timeouts) and passes with the
  fix (15 repeated runs), and `BouncyCastleHandshakeTimeoutTests`, `BouncyCastleHandshakeThreadPoolTests`,
  `BouncyCastleProviderTests`, `BouncyCastleFatalAlertTests`, `BouncyCastleDuplexStreamTests`,
  `BouncyCastleSocketModeTests`, `SslStreamSingleHandshakeTests`, `AbandonableTaskTests` and
  `HeadReadCancellationTests` pass. Mono/IL2CPP behavior is not claimed from that run.
