# 2026-11: HTTP/1.1 repeats a request without Expect after an early 417 (#214)

## What

RFC 9110 Section 10.1.1: a client that receives 417 (Expectation Failed) to a request with a
100-continue expectation SHOULD repeat it without that expectation. The HTTP/1.1 transport returned
the 417 to the caller (phase 22b.1 section 6j, #109). Now it repeats the request once without
`Expect` when:

- the 417 is an early final response, before any body byte was sent
  (`Http11RequestWriteState.HasCommittedBodyBytes` is false; non-100 1xx before it are fine). A 417
  after a 100 or after the wait timeout answers the complete request and is returned;
- `100-continue` is the only expectation (new `ExpectContinueHelper.HasOnlyExpectContinueExpectation`;
  empty list members are ignored, RFC 9110 Section 5.6.1);
- the body is `Replayable` or `ReplayableViaFactory`.

`DispatchOnLeaseAsync` checks this right after the head (`ShouldRetryWithoutExpectation`), records
`TransportExpectationFailedRetry` and throws a private `ExpectationFailedRetrySignal` (a
`UHttpException`, not a network error, so `MapDispatchAttemptFailure` passes it through). The head is
disposed by its `using`; the lease (KeepAlive already false from `CompleteExpectContinueHead`) is
destroyed by the caller's `finally`, with the unread 417 body. `DispatchCoreAsync` catches the signal
in the same single filtered clause as the HTTP/2 replay (IL2CPP, #320), switches to
`CopyWithSharedContent().WithExpectContinue(false)`, sets it as `RequestContext.Request`, and runs
`DispatchAttemptAsync` again under the same request timeout. The 417 never reaches the handler.

## Files

- `Runtime/Transport/RawSocketTransport.cs` (`ShouldRetryWithoutExpectation`,
  `ExpectationFailedRetrySignal`, the retry in `DispatchCoreAsync`, the check in `DispatchOnLeaseAsync`)
- `Runtime/Transport/Internal/ExpectContinueHelper.cs` (`HasOnlyExpectContinueExpectation`)
- `Tests/Runtime/Transport/Http1/RawSocketTransportExpect417RetryTests.cs` (+ `.meta`)
- `Tests/Runtime/Transport/Http1/RawSocketTransportConnectionReuseTests.cs`: the old
  `ExpectContinue_Expect417_ReplayableBody_ReturnedToCallerWithoutRetry` asserted the old behavior and
  is replaced by the new file; the #37 discard test now rejects with 403 (its subject is the unsent
  body after any early final response, and a replayable 417 is now repeated)
- `Development~/docs/phases/phase22b/phase-22b.1-expect-100-continue.md` (6d, 6j, Step 7 item 7a)

## Decisions

- **Explicit and auto-injected `Expect` both retry** (lane note); the issue left "auto-injected
  only" open. The RFC SHOULD does not distinguish them.
- **Retry at the top of the dispatch, not per call site.** `DispatchOnLeaseAsync` has four callers
  (direct, direct stale retry, proxy, proxy stale retry) and the forward-proxy path already uses the
  bool result for its 407 intercept. One signal caught in `DispatchCoreAsync` covers direct,
  forward-proxy and CONNECT-tunnel paths, and the copy without `Expect` bounds it to one repeat by
  construction (it cannot raise the signal again; auto-injection runs once in `DispatchAsync`).
- **Another connection, not necessarily a newly dialed one.** The rejected connection is always
  destroyed (#37); the repeat acquires a connection like any request, so the pool may hand out an
  idle one. The RFC only forbids reusing the connection with the unsent body (RFC 9112 Section 9.6).
- **Other expectations: no retry.** Only `100-continue` is defined; a 417 to `Expect: 100-continue,
  x-other` may be about the other member, and stripping it would change the request's meaning.
- **Non-replayable body: no retry** even though no byte was read (issue text, conservative).
- **`RequestContext.Request` is the repeat**, matching `PrepareEffectiveRequest`, so the response and
  observers see the request that was actually answered.

## Deferred

- HTTP/2: a 417 on an HTTP/2 stream is still returned to the caller (out of scope of #214, which
  names the HTTP/1.1 transport; documented in 6j). Follow-up: #391.

## How verified

- `'./Tools~/ci/check.sh'`: pass.
- New tests: retry with a replayable body (no `Expect` on the repeat, body delivered, rejected
  connection closed without any byte), auto-injected expectation, non-replayable body (417 returned,
  body never read), second 417 returned with exactly two connections, another expectation member, a
  417 after the body was sent on the wait timeout, and the helper's member classification.
- Unity EditMode, scoped: see the PR "Local gates".
- Not run: Unity PlayMode, IL2CPP player, devices (no platform-specific code).
