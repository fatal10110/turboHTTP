# Phase 16.5: Parallel Request Helpers

**Depends on:** Phase 4 (Pipeline Infrastructure)
**Assembly:** `TurboHTTP.Parallel`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 0 modified

---

## Step 1: Implement Core Parallel Request Combinators

**Files:**
- `Runtime/Parallel/ParallelRequestHelpers.cs` (new)
- `Runtime/Parallel/ParallelRequestResult.cs` (new)

Required behavior:

1. Implement `Batch` combinator — execute multiple requests in parallel, return all results:
   - `BatchAsync(UHttpClient client, IEnumerable<UHttpRequest> requests, CancellationToken ct)` → `ParallelRequestResult[]`.
   - All requests execute concurrently up to client's concurrency limit.
   - Wait for all requests to complete regardless of individual failures.
   - Return results in same order as input requests.
   - Each result contains either `UHttpResponse` (success) or `UHttpException` (failure).
2. Implement `Race` combinator — execute multiple requests, return first successful response:
   - `RaceAsync(UHttpClient client, IEnumerable<UHttpRequest> requests, CancellationToken ct)` → `UHttpResponse`.
   - Cancel remaining in-flight requests after first success.
   - If all requests fail, throw `AggregateException` containing all individual exceptions.
   - "Success" is defined as non-exception completion (HTTP 4xx/5xx are still "success" at transport level).
3. Implement `AllOrNone` combinator — execute all requests, succeed only if all succeed:
   - `AllOrNoneAsync(UHttpClient client, IEnumerable<UHttpRequest> requests, CancellationToken ct)` → `UHttpResponse[]`.
   - Cancel remaining requests on first failure.
   - On failure: throw `AggregateException` with all collected exceptions.
   - On success: return all responses in input order.
   - "Failure" is defined as exception thrown during `SendAsync`.
4. Define `ParallelRequestResult` containing:
   - `Request` (UHttpRequest) — the original request.
   - `Response` (UHttpResponse, nullable) — response if successful.
   - `Error` (UHttpException, nullable) — exception if failed.
   - `IsSuccess` (bool) — convenience property.
   - `Index` (int) — original position in input collection.
5. Add optional `maxConcurrency` parameter to `Batch` for local concurrency override (independent of client-level concurrency).

Implementation constraints:

1. Use `Task.WhenAll` / `Task.WhenAny` composition — do not implement custom task scheduling.
2. Cancellation of remaining requests in `Race` and `AllOrNone` must be cooperative via linked `CancellationTokenSource`.
3. Requests must flow through the full `UHttpClient` pipeline (middlewares, transport) — no shortcutting.
4. Input validation: throw `ArgumentException` for null/empty request collections.
5. Local `maxConcurrency` in `Batch` must use `SemaphoreSlim` to gate concurrent sends.
6. All combinators must propagate the caller's `CancellationToken` correctly.
7. Disposed linked `CancellationTokenSource` instances must be cleaned up in `finally` blocks.

---

## Step 2: Add Assembly Definition and Client Extensions

**File:** `Runtime/Parallel/TurboHTTP.Parallel.asmdef` (new)

Required behavior:

1. Configure assembly definition:
   - References: `TurboHTTP.Core`.
   - `autoReferenced: false`.
   - `noEngineReferences: true`.
2. Add extension methods on `UHttpClient`:
   - `BatchAsync(requests, ct)` and `BatchAsync(requests, maxConcurrency, ct)`.
   - `RaceAsync(requests, ct)`.
   - `AllOrNoneAsync(requests, ct)`.
3. Extension methods must be in `TurboHTTP.Parallel` namespace.

Implementation constraints:

1. Follow existing extension method pattern from other modules.
2. Extensions must accept `IEnumerable<UHttpRequest>` (not `List` or `Array`) for flexibility.
3. Internally materialize to array once to avoid multiple enumeration.

---

## Step 3: Add Parallel Request Helper Tests

**File:** `Tests/Runtime/Parallel/ParallelRequestTests.cs` (new)

Required behavior:

1. Validate `Batch` returns all results in correct order with mixed success/failure.
2. Validate `Batch` waits for all requests even when some fail.
3. Validate `Batch` with `maxConcurrency` limits concurrent in-flight requests.
4. Validate `Race` returns first successful response and cancels remaining.
5. Validate `Race` throws `AggregateException` when all requests fail.
6. Validate `AllOrNone` returns all responses on full success.
7. Validate `AllOrNone` cancels remaining and throws on first failure.
8. Validate cancellation token propagation through all combinators.
9. Validate empty request collection throws `ArgumentException`.
10. Validate single-request collection works correctly for all combinators.
11. Validate middleware pipeline integration (requests go through retry, auth, logging).
12. Validate `ParallelRequestResult` properties (`IsSuccess`, `Index`, `Request` reference).
13. Validate linked `CancellationTokenSource` disposal does not leak.

---

## Verification Criteria

1. `Batch` correctly executes all requests in parallel and returns ordered results.
2. `Race` correctly returns first success and cancels remaining in-flight requests.
3. `AllOrNone` correctly fails fast on first error and cancels remaining.
4. Cancellation is cooperative and does not leave orphaned in-flight requests.
5. All combinators integrate with middleware pipeline without bypass.
6. `maxConcurrency` parameter correctly limits parallel execution in `Batch`.
7. No resource leaks from `CancellationTokenSource` or `SemaphoreSlim` under failure paths.
8. Results maintain correlation with original input request order.
