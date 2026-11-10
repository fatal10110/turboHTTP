# 2026-11: the SAEA channel holds its descriptor until its operations ended (#384, #448)

## What

#405 made the NetworkStream path (`HoldingNetworkStream`, the default) keep a socket's descriptor until an abandoned read ended. The SAEA path (`SocketIoMode.Saea`) still closed the socket under a pending operation in two places:

- **Cancellation:** the registration in `ReceiveAsync`/`SendAsync` disposed the socket directly, so it was closed first.
- **Dispose:** it shut the socket down, spin-waited at most 500 ms for the operation to drain, and then closed it anyway.

On IL2CPP a close does not remove the pending job from the selector. The next socket gets the same (lowest free) descriptor number, and its first receive can be dispatched to the stale job and lost. The awaiting caller also waited for an OS completion that IL2CPP does not always post. This fits the 10–15 s "never ended" failures of `SocketChannelLifecycleTests.TokenCancelledByEarlierOperation_ThenReused(False)` and `SaeaShutdownOrderTests` on the iOS simulator.

`SaeaSocketChannel` now works like this:

- **`Dispose`** shuts the socket down (selector platforms) or closes it (IOCP). It fails any pending caller at once with `OperationCanceledException`, and returns without blocking.
- **Closing the socket and returning the buffers** happens on whichever thread sees the last submitted operation complete: a completion handler, or the operation's own `finally` after its copy.
- **Fallback bound:** an operation that never ends is closed at `SocketHandleHold.HoldTimeout` (30 s) on selector platforms, or at 500 ms on IOCP. Its buffers are then leaked on purpose, because the kernel may still write into them (#168).
- **Cancellation** disposes the channel.
- **An OS completion that already arrived keeps its own result**, so the socket error stays the cause (#415). `SaeaCompletionSource` gained `TrySetResult`/`TrySetException`, so the dispose and the late completion cannot both set the same operation.
- **A submitted operation's caller no longer clears the in-flight flag** when it fails. Only the OS completion does, so a failed caller cannot release a descriptor whose operation is still pending.

`Loop384_OneSilentIdle_MaxOnePerHost_200x` and `Loop384_TwoSilentIdle_MaxTwoPerHost_200x` are committed as `[Explicit]` + `[Category("Stress")]` (200 iterations, about 45 s each). They inject the idle connection through `HoldingNetworkStream`, as the pool does. The earlier throwaway loop injected a plain `NetworkStream`, which bypassed #405 and reported 4.8–6.0% failures that came from the test (#448).

## Files

- `Runtime/Transport/Tcp/SaeaSocketChannel.cs`
- `Runtime/Transport/Tcp/SaeaCompletionSource.cs`
- `Tests/Runtime/Transport/SaeaShutdownOrderTests.cs`: the shutdown-order test adapted; new `Cancel_ReceivePending_FailsAtOnceAndHoldsTheDescriptorUntilTheReceiveEnded` and `Dispose_ReceiveNeverEnds_HoldsTheDescriptorUntilTheBound`
- `Tests/Runtime/Transport/SocketChannelLifecycleTests.cs`: the leak is now observed after the bound
- `Tests/Runtime/Transport/Http1/ReusedConnectionFirstByteTimeoutTests.cs`: the loops, and `InjectIdleConnectionAsync` builds a `HoldingNetworkStream`

## Decisions

- **Same bound as `HoldingNetworkStream`:** `SocketHandleHold.HoldTimeout` (30 s). A descriptor held that long is cheaper than a response lost to a stale job.
- **Fail callers at once.** The shutdown alone ends the receive on Android and Linux (#410), but not reliably on IL2CPP/iOS.
- **The shutdown-order test is now an [env] skip, not a failure,** when the receive never ends on the runtime. In that case it asserts that the descriptor is still held.
- **Unchanged:** PollSelect mode still closes its socket at dispose under a blocked poll-thread receive. It does not use the IL2CPP selector's async jobs; it is out of scope here.
- **Unchanged:** a concurrent dispose can return the receive buffer to the pool before a successful receive's continuation copies from it (pre-existing).

## Verification

- `Tools~/ci/check.sh`: OK.
- **Mac Editor**, Unity 2021.3.45f2 PlayMode, filter `Saea|SocketChannel|ReusedConnection|AbandonedSocketIo|AbandonedHandleHold|AbandonedLoopGate|Cancellation|TcpConnectionPool`: 173/173 passed. `Loop384` (by name): 2/2 passed.
- **iOS simulator, 2021.3.45f2 IL2CPP x86_64** (Stripping Low, iOS 26.4 under Rosetta, built on the Mac):
  - Loop build, 3 launches: 53/53 each, both loops passed, 0/1200 iterations failed.
  - Full suite, 2 runs: 2985 passed, 0 failed, 30 skipped (28 known plus the two Explicit loops).
- **iOS simulator, Unity 6000.3.26f1 IL2CPP arm64** (High), loop build, 3 launches: 53/53 each, 0/1200 iterations failed.
- macOS standalone IL2CPP arm64 player (2021.3.45f2, Stripping High, full suite, macOS 26.7.1), 2 runs: 3001 passed, 0 failed, 14 skipped each.
- Not run: Android, a physical iPhone, SAEA mode inside the loops (they run the default NetworkStream mode).
