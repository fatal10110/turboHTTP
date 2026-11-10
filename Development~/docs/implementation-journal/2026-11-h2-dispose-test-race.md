# 2026-11: Http2ConnectionManagerTests waits for the drain-close stream dispose (#445)

## What

`GetIfExists_StaleTunnelConnection_RemovesAndDisposesObservedConnection` failed once in two runs at `main` 3131404, on the macOS Mono standalone player (Unity 2021.3.45f2, native arm64). The failure was `DisposeCallCount` `Expected: 1 But was: 0`.

The cause:
1. The test's GOAWAY makes the read loop dispose the drained connection itself (`TryCompleteDrain(inline: true)`).
2. `Dispose()` cancels `_cts` first, which makes `IsAlive` false, and only later disposes the stream.
3. The test polled `IsAlive` and read the count once, in between.

The test now waits (bounded, `AbandonedSocketIoTests.WaitUntilAsync`) for the count to reach 1 and then asserts it is exactly 1. The tracking stream's counter is now `Interlocked`/`Volatile`, because another thread writes it.

## Files

- `Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs`

## Decisions

- Test-only. Product disposal is correct: `DisposeStreamOnce` runs exactly once, on the read-loop thread.

## Verification

- `Tools~/ci/check.sh` passed.
- Not run: the fixed test in Unity EditMode/PlayMode or a player (no Unity on the Mac). The next macOS player run will confirm it.
