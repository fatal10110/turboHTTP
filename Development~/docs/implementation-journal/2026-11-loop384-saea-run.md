# 2026-11 Loop384 SAEA variants: Editor and Win64 IL2CPP runs (#1184)

Test-run record for the SAEA-mode `Loop384_*` loops added for #1184 at commit 32b0760 (origin/main
2de7cd3 plus the test change). The change and its decisions are in the PR. Setup: 2026-11-23, Unity
2021.3.45f2, Windows host editor started from WSL2, one Unity process at a time
(`flock /tmp/turbohttp-unity.lock`). The `--filter` regex selects `[Explicit]` tests it names.

The SAEA variants run the pool with `SocketIoMode.Saea` and inject each silent idle connection as a
`SaeaStream` over a `SaeaSocketChannel`, so the abandoned read goes through the SAEA descriptor hold
of PR #455. The NetworkStream loops are unchanged.

## Commands

```
flock /tmp/turbohttp-unity.lock './Tools~/unity/run-tests.sh' --platform PlayMode --filter 'ReusedConnectionFirstByteTimeoutTests'
flock /tmp/turbohttp-unity.lock './Tools~/unity/run-tests.sh' --platform StandaloneWindows64 --backend IL2CPP --filter 'ReusedConnectionFirstByteTimeoutTests'
flock /tmp/turbohttp-unity.lock './Tools~/unity/run-tests.sh' --platform PlayMode --filter 'ReusedConnectionFirstByteTimeoutTests\.Loop384'
```

## Results

| Target | Backend, stripping | Filter | Total | Passed | Failed |
| --- | --- | --- | --- | --- | --- |
| Windows Editor PlayMode, run 1 | Mono, n/a | whole fixture | 11 | 10 | 1 |
| Win64 standalone player | IL2CPP, High | whole fixture | 11 | 11 | 0 |
| Windows Editor PlayMode, run 2 | Mono, n/a | `Loop384` only | 4 | 4 | 0 |

Loop iterations (200 per loop and run):

| Loop | Editor run 1 | Win64 IL2CPP | Editor run 2 |
| --- | --- | --- | --- |
| `Loop384_Saea_OneSilentIdle_MaxOnePerHost_200x` | 200/200 | 200/200 | 200/200 |
| `Loop384_Saea_TwoSilentIdle_MaxTwoPerHost_200x` | 200/200 | 200/200 | 200/200 |
| `Loop384_OneSilentIdle_MaxOnePerHost_200x` (NetworkStream) | 199/200 | 200/200 | 200/200 |
| `Loop384_TwoSilentIdle_MaxTwoPerHost_200x` (NetworkStream) | 200/200 | 200/200 | 200/200 |

- SAEA mode: 0 of 1200 iterations failed (2 loops x 3 runs), including 400 under IL2CPP at High stripping.
- The one failure was in the unchanged NetworkStream loop, Editor run 1, iteration 8:
  `SocketException` "the system lacked sufficient buffer space or because a queue was full"
  (WSAENOBUFS) with `accepts=-1`. `accepts` stays -1 only when `SilentThenOkServer`'s constructor
  throws, so `TcpListener.Start` on loopback port 0 failed before any request was sent. That is a host
  socket-resource failure, not the #384 failure mode (a lost or late retry response). It did not recur in
  the IL2CPP run or in Editor run 2.
- Each loop took about 41-42 s on both targets.

Not run: the iOS simulator (200 x 3 launches, #1184 criterion 2) and an Android emulator (criterion 3);
both stay open on #1184.
