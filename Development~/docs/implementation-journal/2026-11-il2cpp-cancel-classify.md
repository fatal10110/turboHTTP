# 2026-11: IL2CPP catch-clause miscompilation, cancellation classification, HTTP/2 cancel RST race (#320, #318)

## What
- **Root cause of #320** (recorded on the issue before the fix): IL2CPP (Unity 2021.3.45f2) emits the catch
  clauses of a `try` that has an exception filter (`catch ... when`) as nested C++ `try` blocks. Every clause
  but the last runs inside the C++ `try` whose `catch` implements the later clauses, so an exception thrown or
  rethrown by an earlier clause is caught by a later clause of the same C# `try` (ECMA-335 sends it to the
  enclosing region). A `try` without filters is emitted correctly. Mono (Editor) is unaffected.
  Evidence: a probe matrix on an IL2CPP player (x86_64 emulator API 33, Stripping High) and the generated C++
  of the arm64 device build; every #320 symptom traced to one such `try`
  (Http2Connection.DispatchAsync, MockTransport, RawSocketTransport.DispatchAttemptAsync,
  WebSocketHandshakeValidator, ResponseBodyDiscardHelper, ...). Stripping and exception types were not involved
  (`await` of a canceled task throws `TaskCanceledException`, `is OperationCanceledException` holds).
- **Fix**: every `try` with a filter now has at most one throwing clause, the last. Pass-through clauses
  (`catch (X) when (c) { throw; }`) fold into the next clause's filter (`when (!(ex is X && c))`); mapping
  ladders become one clause that dispatches with `if` (RawSocketTransport: `MapDispatchAttemptFailure`).
  Sites: Http2Connection, RawSocketTransport (dispatch ladder), Http11ResponseBodySource, TcpConnectionPool,
  Nat64AddressSynthesizer, BouncyCastleTlsProvider, ResponseBodyDiscardHelper, MockTransport,
  RecordReplayTransport, ObservedResponseBodySource, CacheStoringHandler, CountingResponseBodySource,
  ConcurrencyLimiter, LiteJsonSerializer, WebSocketHandshakeValidator, WebSocketFrameReader,
  WebSocketConnection.SendReceive (two), RawSocketWebSocketTransport (two), and two test helpers.
- **Invariant**: `check_repo.py il2cppfilter` fails a non-last catch clause that throws in a `try` with a
  filter (clauses whose body awaits are exempt: the compiler runs their body after the `try`).
  `Tools~/ci/test_il2cpp_filter_check.py` pins the flagged and accepted shapes (run by `check.sh --full`).
- **Defence in depth** (Mono-testable): both dispatch bridges surface a wrapped cancellation
  (`UHttpException`/`AggregateException`/handler fault around any `OperationCanceledException`) as
  `OperationCanceledException` with the caller's token when that token fired
  (`Core/Internal/CancellationClassification`); without the caller's token the error is kept (a deadline stays
  `Timeout`). The HTTP/2 retry loop finds `Http2RetryableRequestException` inside `UHttpException`/single
  `AggregateException` wrappers (`RawSocketTransport.FindH2Retryable`).
- **#318**: `Http2Connection.HandleRequestCancellation` faulted the stream before claiming it. Faulting resumes
  `DispatchAsync` on a pool thread, whose `finally` removes a stream whose response has not started; when that
  won the race, neither path sent RST_STREAM(CANCEL) and the peer kept the stream open, which is what the
  scenario's watchdog reported. The callback now claims the stream (instance-exact) first, then faults it and
  sends the reset. The scenario already waited on the RST_STREAM event (ScriptedHttp2Server.WaitForAsync); the
  30 s token is only a watchdog, so the test was not changed.

## Decisions
- Fix the code shape everywhere instead of only the sites the failing tests hit: the defect is in code
  generation, and the untested sites (cache/observability cancel paths, limiter dispose, TLS cancel vs.
  deadline, WebSocket control-frame cancel) fail the same way on players.
- The bridges' canceled-task branch keeps its exact-type test: it is not hazardous (its handlers do not
  throw) and it preserves subtypes such as `BackgroundRequestQueuedException`.
- WebSocketFrameReader now returns the payload buffer on the cancellation-mapped failures too (the old
  ladder kept it out of the pool on that path).

## Deferred
- No deterministic unit test reproduces the #318 interleaving (it needs the dispatch continuation to run
  between two statements of the cancel callback); the adversarial scenario and repeated host-editor runs
  cover it.

## Verification
- `'./Tools~/ci/check.sh'`: pass (il2cppfilter: 0 problems).
- New tests: `Tests/Runtime/Pipeline/DispatchBridgeCancellationClassificationTests.cs`,
  `Tests/Runtime/Transport/RawSocketTransportFailureMappingTests.cs`, `Tools~/ci/test_il2cpp_filter_check.py`.
- Host editor (Unity 2021.3.45f2, Mono), PlayMode scoped filter (bridges, HTTP/2, WebSocket, client, cache,
  observability, limiter, transport fixtures): 591 passed, 0 failed, 1 skipped (baseline skip).
- Host editor PlayMode `--filter 'Http2AdversarialServerTests'`, three separate runs at 707c924: 15/15 each (#318).
- IL2CPP, x86_64 emulator API 33, Unity 2021.3.45f2, Stripping High, at 707c924,
  `--filter 'UHttpClientTests|MockHttpServerTests.InjectedDelay_RespectsCancellation|RedirectInterceptorTests|BackgroundNetworkingTests|StressTests.CancellationStorm|Http2ConnectionTests|Http2AdversarialServerTests|RawSocketTransportHttp2ConnectionManagementTests|WebSocketConnectCancellationTests|DispatchBridgeCancellationClassificationTests|RawSocketTransportFailureMappingTests|RecordReplayTransportTests'`:
  335 total, 333 passed, 1 failed, 1 skipped. Every #320 test (including the 2026-11-03 comment's
  WebSocketConnectCancellationTests.Upgrade_* and RawSocketTransportHttp2ConnectionManagementTests) passes. The failure is
  `SendRequest_WithInvalidRequestHeader_*("X-Bad\0","value")`: IL2CPP truncates the attribute string at the NUL, so the
  request is valid and waits for a response until the test's 10 s token fires. That is the test-data defect #321 item 5
  (player-ready-tests lane), not this change.
- A second emulator run with a narrower filter (`Http2ConnectionTests|Http2AdversarialServerTests|RawSocketTransportHttp2ConnectionManagementTests`)
  stalled: from `SendRequest_Authority_*` on, every test failed after about 20 s with the player at 0% CPU. The same fixtures
  passed in the first run at the same head. This matches the starved-pool/hang pattern of #330, but the cause was not
  determined; the run was stopped to free the shared lock.
- Not run: the phone (OnePlus 7, 80917dd4) re-run: the Family Link lock screen holds focus, so the player never starts
  (needs-device-validation; #320 stays open for it). Also not run: the full Unity suites (periodic host-editor run), iOS.
