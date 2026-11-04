# 2026-10 request-pool: single-lease UHttpRequest (#62), hold-count clamp and lifecycle tests (#148)

## What

- **#62 (stale reference after re-lease).** `UHttpClient.CreateRequest` handed out the same pooled
  `UHttpRequest` instance again after it was returned, so a caller that kept a reference (a retry loop, a
  captured lambda) passed `ThrowIfDisposed` once the object was re-leased and mutated or sent another
  caller's request (headers, credentials). A lease generation stored inside the object cannot fix this:
  the stale holder and the new holder share the same reference. `CreateRequest` now allocates a new
  request per call and the lease is one-shot (`_isLeased` goes 1 -> 0 exactly once and is never set back),
  so every later mutation or send through a stale reference throws `ObjectDisposedException`, and even a
  cached `Headers`/`Metadata` object can no longer alias another caller's request.
- **#148 (negative hold-count branch).** `ReleaseResponseHold` decremented first and, on a negative
  result, reset to 0 and returned without trying to complete the lease. A transient negative count made a
  concurrent `EndSend`/`Dispose` skip completion, and the early return skipped it too, so the lease leaked.
  The count is now decremented only while positive (CAS loop, never observed below zero) and completion
  is always attempted afterwards; `TryCompleteLease` is idempotent through the `_isLeased` CAS.
- Lease completion (`TryReturnToPool` -> `TryCompleteLease`) now disposes the owned body content instead
  of resetting the object and returning it to an `ObjectPool<UHttpRequest>`; the request pool,
  `ActivateLease`, `ResetForPool` and `UHttpClient.ReturnRequestToPool` are removed. Internal
  `IsPooled` is renamed `IsClientOwned`; internal `IsLeaseActive` is added for tests.
- The 2026-02 journal (`2026-02-phase19b-review-fixes.md`, Bug 1) claimed the early `return` was already
  removed. It was not (still in `UHttpRequest.cs` on main 068da8c); this entry supersedes that claim.

## Files

- `Runtime/Core/UHttpRequest.cs`: client-owned constructor takes method/URI/timeout; lease-state ownership
  comment; `TryCompleteLease`; clamped `ReleaseResponseHold`; docs on the class and `Dispose`.
- `Runtime/Core/UHttpClient.cs`: request pool removed; `CreateRequest` allocates; cross-client guard text.
- `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`, `StreamingDispatchBridge.cs`: `IsClientOwned`.
- `Runtime/Core/UHttpResponse.cs`: finalizer comment no longer refers to pool return.
- `Tests/Runtime/Core/UHttpRequestLeaseLifecycleTests.cs` (new, + `.meta`); the two
  `..._KeepsPooledRequestOutOfPoolUntilResponseDisposed` tests in `UHttpClientTests.Execution.cs` became
  `..._KeepsRequestLeaseActiveUntilResponseDisposed` (they asserted `AreSame(request, recycled)`, which is
  exactly the #62 aliasing).
- Docs: `Development/docs/agents/architecture.md` (Core pooling note), `Documentation~/APIReference.md`,
  `Documentation~/Modules/Core.md`.

## Decisions

- **Un-pool the user-facing request (issue option 2) rather than a handle/token.** A per-lease handle
  wrapping pooled state would keep the public type but still leaks through cached `Headers`/`Metadata`
  object references and check-then-act races (a stale call passing the generation check just before the
  state is recycled). Not reusing the object closes every path with no locking. Cost: `CreateRequest`
  allocates a `UHttpRequest`, an `HttpHeaders` and a metadata dictionary per request (the pooled path
  already allocated an `EmptyRequestBody` per lease and per reset). Credentials crossing requests
  (CLAUDE.md section 3) outranks this allocation (section 4).
- **No public signature change.** `CreateRequest`/`Get`/`Post` still return `UHttpRequest`; the observable
  change is that a disposed request is never handed out again.
- **Unmatched release = clamp + complete.** A double release is a caller bug; clamping keeps the next
  `BeginSend` usable and completion stays exactly-once by the CAS.

## Verification

- `./Tools~/ci/check.sh` (invariants, format, shadow build with analyzers): OK.
- The new fixture (18 cases) was also compiled and run outside Unity with `dotnet test` against the
  shadow-built Core/Testing/Transport assemblies plus a two-member `UnityEngine.CoreModule` stub:
  18/18 pass. They were not run against the old code (they use the new internal `IsLeaseActive`); the
  stale-reference assertion `AreNotSame` contradicts the old `AreSame(request, recycled)` tests by construction.
- **Not run:** Unity EditMode/PlayMode (host cannot drive Unity; PR CI runs them), IL2CPP, devices.
  The edited `UHttpClientTests.Execution.cs` tests run only on CI.

## Deferred

- None.
