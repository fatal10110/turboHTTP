# CodeQL review-thread cleanup after waves run w1030

## What

Waves run w1030 merged PRs #194, #195, #198, #200, #201 and #202 with 36 unresolved review threads.
35 were CodeQL alerts from `github-advanced-security`; one was a reviewer `[note]` on #200. No agent
owned the bot threads (fixed in the waves tooling by #203). This entry records how each was triaged.

## Fixed (this PR)

| Alert | File | Fix |
|---|---|---|
| 763 useless assignment | `Tests/Runtime/Transport/Http2/Http2ConnectionTests.WritePath.cs` | second `clientBytesRead +=` is never read again: pass `clientBytesRead + windowUpdateFrameSize` |
| 768 dispose on throw | `Tests/Runtime/Transport/Http11ResponseParserStrictnessTests.cs` | `head.Dispose()` moves to a `finally`, so a throwing `Http11ResponseBodySource` constructor no longer leaks it |
| 773 useless assignment | `Tests/Runtime/Unity/UnityWebSocketBackpressureTests.cs` | `var _ =` becomes a discard; the statement only forces `MainThreadDispatcher` creation |

## Resolved with a reason, alert dismissed

- **Runtime callback isolation** (alert 775 `ResilientWebSocketClient.cs:442`, alert 776 `WebSocketClient.cs:467`): the catch-all routes any exception from the user's `OnMessage` handler to `OnError`. It must not let a user exception kill the receive loop. Won't fix.
- **Null dereference** (alert 772, `WebSocketClient.cs:389`): the field is only ever assigned a non-null queue (`ConnectAsync`, `:143`), so the nullness alert is a false positive. The `?.` checks CodeQL pairs it with are in the listener's own `finally` (`:432`) and in `HandleConnectionStateChanged` (`:484`). The #204 review found a real defect at the same place: on reconnect the old listener is never cancelled and completes the *new* queue with the old error, filed as #208 (not fixed here).
- **`Path.Combine`** (alert 718, alert 719 in `Editor/TurboHttpLinkerProcessor.cs`; alert 720–alert 722 in its tests): the later arguments are assembly names plus `.dll` or a GUID-named temp folder, never rooted paths. `Path.Join` is not available to Editor code on the .NET Framework API level. False positive.
- **Test helpers that must swallow every exception** (alert 714, alert 717 `CaptureFailure` returns the exception to assert on; alert 715, alert 712, alert 716 `LocalTlsTestServer` accept loop and teardown; alert 777, alert 778 `InMemoryWebSocketPeer` server loop and `SafeDispose`): intended and commented. Used in tests.
- **Explicit `Dispose` in lifecycle tests** (alert 783–alert 790 `UHttpRequestLeaseLifecycleTests`): the tests assert lease state at the exact point of each `Dispose`, including double dispose. `using` would change what they test. Used in tests.
- **`TcpListener` not disposed** (alert 713 `TcpConnectionPoolTlsFallbackTests.cs:92`): `Stop()` runs in `finally`; `TcpListener` is not `IDisposable` in the Unity 2021.3 profiles. Used in tests.
- **Style** (alert 723, alert 724, alert 774 LINQ `Select`/`Where`; alert 779–alert 782 `using` instead of `finally` in a coroutine test and in message handlers): no behaviour change. Won't fix.
- **#200 reviewer note** on `Http11ResponseParser.cs:283`: the replay exposure is tracked by #48 (h1-reuse lane).

## Verification

- `./Tools~/ci/check.sh`: OK.
- Unity EditMode/PlayMode: not run. PR CI skips them (no `UNITY_LICENSE`, #188), so the edited tests only compiled in the shadow build; they get their first Unity run in the next host-editor run (#207).
