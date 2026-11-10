# 2026-11: AbandonedHandleHoldTests waits for the socket dispose, not the release flag (#442)

## What

`Close_ReadNeverEnds_HandleIsReleasedAtTheHoldBound` failed once in two runs at `main` 3131404, on the Unity 2021.3.45f2 IL2CPP x86_64 iOS simulator player (iPhone 17 Pro, iOS 26.4). The assert that failed was `Expected: True But was: False` on `IsClosed(client)`.

The cause is in the order `SocketHandleHold.Release` does things:
1. sets `_released`;
2. disposes the socket;
3. writes `_releaseKind`.

At the hold bound, `Release` runs on the timer thread. So `IsReleased` can be true while that thread has not yet disposed the socket. The test now waits on `ReleaseKind`, which is written after the dispose.

## Files

- `Tests/Runtime/Transport/AbandonedHandleHoldTests.cs`

## Decisions

- Test-only change. `IsReleased` is the hold's once-only guard and is meant to flip first. Moving it after the dispose would let a second `Release` caller dispose the socket twice.
- This is the only test in the fixture that waits for a release on another thread. The others release on the test thread.

## Verification

- `Tools~/ci/check.sh` (shadow build) passed.
- The failure was seen once in 2 simulator runs (run 1: 2982 passed / 1 failed / 28 skipped; run 2: 2983 / 0 / 28).
- Not run: the fixed test on the simulator or a device, and the Unity EditMode/PlayMode suites (no Unity on the Mac). The fix will be checked by the next simulator run.
