# Phase 19.1: ValueTask Migration

**Depends on:** None (foundational)
**Estimated Effort:** 1 week

---

## Step 0: Audit Current Async Surface

Required behavior:

1. Inventory all public and internal async method signatures across Core, Pipeline, Middleware, Transport, and Observability assemblies.
2. Categorize each method into one of: **interface definition**, **delegate type**, **public API**, **internal implementation**.
3. Identify which methods already use `ValueTask` (e.g., `IHttpInterceptor`, `IHttpPlugin`) and mark them as no-change.
4. Document the complete migration list with current and target return types.
5. Identify all call sites of `AdaptiveMiddleware` that bridge `ValueTask` → `Task` — these bridges will be removed after migration.

Implementation constraints:

1. The audit is a documentation step — no code changes. Output a checklist of all methods to migrate with file paths.
2. Group methods by dependency order: interfaces and delegates first, then implementations that depend on them.
3. Flag any methods that store or multi-await `Task` results — these are `ValueTask` migration hazards.

---

## Step 1: Migrate Core Delegate and Interface Definitions

Required behavior:

1. Migrate `HttpPipelineDelegate` from `Func<UHttpRequest, Task<UHttpResponse>>` → `Func<UHttpRequest, ValueTask<UHttpResponse>>`.
2. Migrate `IHttpMiddleware.InvokeAsync` return type from `Task<UHttpResponse>` → `ValueTask<UHttpResponse>`.
3. Migrate `IHttpTransport.SendAsync` return type from `Task<UHttpResponse>` → `ValueTask<UHttpResponse>`.
4. Update any other delegate types or interfaces that return `Task` in the pipeline hot path.

Implementation constraints:

1. These are breaking changes to interfaces — since TurboHTTP has no released public API contract, direct migration is acceptable (no dual-interface period).
2. After changing interface definitions, the project will not compile until all implementations are updated (Steps 2-4). This is expected — batch the changes.
3. Ensure `using System.Threading.Tasks` is present in all modified files (for `ValueTask<T>`).
4. Do not change method names or parameters — only return types.

---

## Step 2: Migrate All Middleware Implementations

Required behavior:

1. Update all 15+ middleware implementations to return `ValueTask<UHttpResponse>` from their `InvokeAsync` methods.
2. Target directories: `Auth/`, `Cache/`, `Middleware/`, `Observability/`, `Performance/`, `Retry/`.
3. For middleware that simply `await` and return, change return type and the method compiles as-is (C# compiler generates `ValueTask`-returning async state machines).
4. For middleware with synchronous fast paths (e.g., cache hits, short-circuit responses), convert to return `new ValueTask<UHttpResponse>(result)` directly without async/await — this is the zero-allocation benefit.

Implementation constraints:

1. **Identify synchronous fast paths:** Review each middleware for early-return code paths that don't actually await anything. These are prime candidates for non-async `ValueTask` returns.
2. For middleware with mixed paths (sometimes sync, sometimes async), use the `async ValueTask<T>` pattern — the compiler handles both cases.
3. Do not refactor middleware logic — only change return types. Logic refactoring is out of scope.
4. Update `next` delegate parameter types from `HttpPipelineDelegate` (which now returns `ValueTask`) — all downstream calls naturally become `ValueTask`.

---

## Step 3: Migrate Public API Surface

Required behavior:

1. Migrate `UHttpClient.SendAsync` to return `ValueTask<UHttpResponse>`.
2. Migrate `HttpPipeline.ExecuteAsync` to return `ValueTask<UHttpResponse>`.
3. Update convenience methods (`GetAsync`, `PostAsync`, `PutAsync`, `DeleteAsync`, `PatchAsync`) if they wrap `SendAsync`.
4. Ensure all public extension methods on the client return `ValueTask` where appropriate.

Implementation constraints:

1. Public API methods that callers may `.Result` or `.GetAwaiter().GetResult()` on — `ValueTask` supports this only if consumed exactly once. Document this constraint in XML doc comments.
2. Add XML doc comment warning on all public `ValueTask`-returning methods: `/// <remarks>The returned ValueTask must be awaited exactly once and must not be stored for later consumption.</remarks>`.
3. Methods returning `ValueTask` must not be used with `Task.WhenAll` or `Task.WhenAny` directly — document that callers should use `.AsTask()` if they need `Task` combinators.

---

## Step 4: Remove AdaptiveMiddleware ValueTask→Task Bridge

Required behavior:

1. Remove the `ValueTask` → `Task` conversion bridge in `AdaptiveMiddleware` that currently wraps interceptor/plugin `ValueTask` results into `Task` for the pipeline.
2. Since the entire pipeline is now `ValueTask`-first, this bridge is unnecessary — interceptors, plugins, and middleware all speak the same async currency.
3. Simplify `AdaptiveMiddleware` to pass through `ValueTask` results directly.

Implementation constraints:

1. Verify that removing the bridge does not break the middleware ordering or chaining logic.
2. The `AdaptiveMiddleware` may have `.AsTask()` calls or `Task.FromResult` wrappers — remove all of these.
3. Run a mental trace through the full pipeline path (client → pipeline → middleware chain → transport → response) to verify `ValueTask` flows end-to-end without conversion.

---

## Step 5: Compile and Fix Cascade

Required behavior:

1. Perform a full project build after all migrations in Steps 1-4.
2. Fix all remaining compilation errors caused by the migration (missed call sites, test helpers returning `Task`, etc.).
3. Update test project method signatures where test methods call migrated APIs.
4. Verify no `ValueTask` is being multi-awaited or stored in fields/collections.

Implementation constraints:

1. Search for patterns: `.Result`, `.GetAwaiter().GetResult()`, `await await`, storing `ValueTask` in variables that are awaited in multiple branches — these are all `ValueTask` misuse patterns.
2. Search for `Task.WhenAll` or `Task.WhenAny` calls that accept migrated methods — these need `.AsTask()` wrappers.
3. Ensure test assertions still work with `ValueTask` (xUnit/NUnit `Assert.ThrowsAsync` works with `ValueTask` via `.AsTask()`).
4. Do not suppress warnings — fix each one.

---

## Verification Criteria

1. Full project compiles with zero errors and zero warnings related to the migration.
2. All existing unit and integration tests pass without modification (beyond return type changes).
3. No `ValueTask` instance is stored in a field, collection, or awaited more than once — verified by code review / grep.
4. `AdaptiveMiddleware` no longer contains any `Task` conversion code.
5. `IHttpInterceptor`, `IHttpPlugin`, `IHttpMiddleware`, `IHttpTransport`, `HttpPipelineDelegate`, `UHttpClient.SendAsync`, and `HttpPipeline.ExecuteAsync` all return `ValueTask<UHttpResponse>`.
6. Synchronous fast paths in middleware return `ValueTask` without async state machine allocation (verified by checking compiled output or allocation benchmark).
