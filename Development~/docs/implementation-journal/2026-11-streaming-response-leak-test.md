# 2026-11 — Streaming response leak in a PlayMode test (#366)

## What

iOS simulator runs logged `[TurboHTTP] UHttpStreamingResponse was not disposed` from the response
finalizer, under an unrelated test. The leaker is a test, not the product:
`Http2ConnectionTests.FlowControl_PeerUsesStreamCreditBeforeWindowUpdateWriteReturns_NoFlowControlError`
(added with #224). Its helper kept the streaming response in a local, never disposed on any path.

How it was found: a temporary hook stored the current test name and creation stack in each response
and printed them from the finalizer; a scoped PlayMode run over the streaming fixtures (666 tests)
logged one warning, at editor shutdown, created by `StreamingDispatchBridge` over an HTTP/2 stream in
that test. The hook was removed; the permanent guard below reproduces the finding.

## Files

- `Runtime/Core/UHttpStreamingResponse.cs`: the finalizer also increments a static counter
  (`UndisposedFinalizedCountForTesting`, internal, same `DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD`
  guard as the warning; `Interlocked` on the finalizer thread, `Volatile` read).
- `Tests/Runtime/StreamingResponseLeakCheck.cs`: `Begin()` / `AssertNoLeakSince()` (GC + pending
  finalizers, then compare the counter).
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.LeakCheck.cs`: `[SetUp]`/`[TearDown]` that fail
  any `Http2ConnectionTests` test dropping a streaming response.
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.FlowControlWakeup.cs`: the response is handed to
  the test's `finally` (`StrongBox`) and disposed after the held write is released and the connection
  disposed.
- `Tests/Runtime/Core/UHttpStreamingResponseLeakCounterTests.cs`: the counter counts dropped responses
  and not disposed ones.

## Decisions

- Unity Test Framework 1.1.33 only runs `ITestAction` attributes placed on test methods (checked in
  its `TestActionCommand`; an assembly- or class-level action never ran), so the guard is a
  `[SetUp]`/`[TearDown]` pair in a partial of the fixture instead of an assembly-wide attribute.
- Scoped to `Http2ConnectionTests` (205 tests, +2.7 s in PlayMode): each check forces two full
  collections. The runtime hunt showed no other leaker in the streaming fixtures, so no other fixture
  pays for it.
- A counter instead of a diagnostic-sink capture: other tests replace the process-wide sink.
- The counter test captures the sink (no stray warning in the run log) and disposes, through weak
  references, the instances a conservative stack scan kept alive, so none finalizes later as a warning
  under another test.

## Verification

- Guard on the unfixed test: PlayMode `--filter 'Http2ConnectionTests'` 204/205, the leaker failed in
  TearDown with "1 UHttpStreamingResponse instance(s) were finalized without Dispose".
- After the fix: PlayMode over the streaming fixtures (689 tests) pass, no "was not disposed" in the
  editor log.
- `'./Tools~/ci/check.sh'`: OK.

## Not run

- No full iOS simulator or Android emulator run: acceptance criterion 2 of #366 stays open (the PR is
  "Part of #366"). The next device run (#99) should show no warning.
- No full Unity suite locally (periodic host-editor run).
