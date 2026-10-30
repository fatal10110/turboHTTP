# CodeQL review-thread cleanup after waves run w1030

## What

Waves run w1030 merged PRs #194, #195, #198, #200, #201 and #202 with 36 unresolved review threads.
35 were CodeQL alerts from `github-advanced-security`; one was a reviewer `[note]` on #200. No agent
owned the bot threads (fixed in the waves tooling by #203). This entry records how each was triaged.

## Fixed (this PR)

| Alert | File | Fix |
|---|---|---|
| #763 useless assignment | `Tests/Runtime/Transport/Http2/Http2ConnectionTests.WritePath.cs` | second `clientBytesRead +=` is never read again: pass `clientBytesRead + windowUpdateFrameSize` |
| #768 dispose on throw | `Tests/Runtime/Transport/Http11ResponseParserStrictnessTests.cs` | `head.Dispose()` moves to a `finally`, so a throwing `Http11ResponseBodySource` constructor no longer leaks it |
| #773 useless assignment | `Tests/Runtime/Unity/UnityWebSocketBackpressureTests.cs` | `var _ =` becomes a discard; the statement only forces `MainThreadDispatcher` creation |

## Resolved with a reason, alert dismissed

- **Runtime callback isolation** (#775 `ResilientWebSocketClient.cs:442`, #776 `WebSocketClient.cs:467`): the catch-all routes any exception from the user's `OnMessage` handler to `OnError`. It must not let a user exception kill the receive loop. Won't fix.
- **Null dereference** (#772 `WebSocketClient.cs:389`): `_messageQueue` is assigned in `ConnectAsync` before the receive listener starts; the null check CodeQL pairs it with is on the dispose path. False positive.
- **`Path.Combine`** (#718, #719 in `Editor/TurboHttpLinkerProcessor.cs`; #720–#722 in its tests): the later arguments are assembly names plus `.dll` or a GUID-named temp folder, never rooted paths. `Path.Join` is not available to Editor code on the .NET Framework API level. False positive.
- **Test helpers that must swallow every exception** (#714, #717 `CaptureFailure` returns the exception to assert on; #715, #712, #716 `LocalTlsTestServer` accept loop and teardown; #777, #778 `InMemoryWebSocketPeer` server loop and `SafeDispose`): intended and commented. Used in tests.
- **Explicit `Dispose` in lifecycle tests** (#783–#790 `UHttpRequestLeaseLifecycleTests`): the tests assert lease state at the exact point of each `Dispose`, including double dispose. `using` would change what they test. Used in tests.
- **`TcpListener` not disposed** (#713 `TcpConnectionPoolTlsFallbackTests.cs:92`): `Stop()` runs in `finally`; `TcpListener` is not `IDisposable` in the Unity 2021.3 profiles. Used in tests.
- **Style** (#723, #724, #774 LINQ `Select`/`Where`; #779–#782 `using` instead of `finally` in a coroutine test and in message handlers): no behaviour change. Won't fix.
- **#200 reviewer note** on `Http11ResponseParser.cs:283`: the replay exposure is tracked by #48 (h1-reuse lane).

## Verification

- `./Tools~/ci/check.sh`: OK.
- Unity EditMode/PlayMode: not run locally (Unity cannot be driven from this WSL host); CI runs them.
