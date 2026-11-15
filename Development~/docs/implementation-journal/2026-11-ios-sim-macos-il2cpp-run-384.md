# 2026-11 iOS simulator / macOS IL2CPP run for #384

Run record for the SAEA descriptor hold (PR #455) at commit 8d35e82. Decisions and the change itself are
in the PR. All runs were on the author's Mac.

| Target | Unity | Backend, stripping | Suite | Result |
| --- | --- | --- | --- | --- |
| Mac Editor PlayMode | 2021.3.45f2 | Mono | filter `Saea\|SocketChannel\|ReusedConnection\|AbandonedSocketIo\|AbandonedHandleHold\|AbandonedLoopGate\|Cancellation\|TcpConnectionPool` | 173/173 passed |
| Mac Editor PlayMode | 2021.3.45f2 | Mono | `Loop384_*` by name | 2/2 passed |
| iOS simulator (iOS 26.4, x86_64 under Rosetta) | 2021.3.45f2 | IL2CPP, Low | loop build, 3 launches | 53/53 each; 0/1200 loop iterations failed |
| iOS simulator (iOS 26.4, x86_64 under Rosetta) | 2021.3.45f2 | IL2CPP, Low | full suite, 2 runs | 2985 passed, 0 failed, 30 skipped (28 known + the 2 Explicit loops) |
| iOS simulator (arm64) | 6000.3.26f1 | IL2CPP, High | loop build, 3 launches | 53/53 each; 0/1200 loop iterations failed |
| macOS standalone player (macOS 26.7.1, arm64) | 2021.3.45f2 | IL2CPP, High | full suite, 2 runs | 3001 passed, 0 failed, 14 skipped each |

Not run: Android, a physical iPhone, and SAEA mode inside the `Loop384_*` loops (they run the default
NetworkStream mode), so the SAEA change itself is not device-validated under the #364 scenario.
