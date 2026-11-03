# Phase 19.2: Pipeline & Transport Migration

**Depends on:** 19.1 (ValueTask Migration)
**Estimated Effort:** 1 week

---

## Step 0: Audit Transport Layer Async Methods

Required behavior:

1. Inventory all async methods in the transport layer: `TcpConnectionPool`, `Http2ConnectionManager`, `TlsStreamWrapper`, `HappyEyeballsConnector`, and related classes.
2. Identify synchronous fast paths — methods that return cached or pooled results synchronously most of the time but occasionally need async I/O.
3. Categorize each method by optimization priority:
   - **High ROI:** Methods with synchronous fast paths called per-request (e.g., connection pool lookup, HTTP/2 connection reuse).
   - **Low ROI:** Methods called once per connection establishment (e.g., TCP connect, TLS handshake) — `ValueTask` saves one allocation but the I/O cost dominates.
4. Document each method's current signature, expected fast-path frequency, and target signature.

Implementation constraints:

1. This is a documentation/analysis step — no code changes.
2. Focus on high-ROI targets first. Low-ROI targets should still be migrated for API consistency but are lower priority.
3. Note any methods that use `TaskCompletionSource` — these are candidates for `IValueTaskSource` refactoring in Phase 19.3, not this sub-phase.

---

## Step 1: Migrate Http2ConnectionManager

Required behavior:

1. Migrate `Http2ConnectionManager.GetOrCreateAsync` to return `ValueTask<Http2Connection>`.
2. The synchronous fast path (cached alive connection found in the pool) must return `new ValueTask<Http2Connection>(connection)` directly — zero allocation on the hot path.
3. The async slow path (need to create new connection, perform ALPN negotiation, send SETTINGS preface) uses the standard `async ValueTask<Http2Connection>` pattern.
4. Update all call sites of `GetOrCreateAsync` to handle `ValueTask` correctly.

Implementation constraints:

1. `Http2ConnectionManager` likely holds a lock or semaphore during connection creation — ensure the `ValueTask` pattern doesn't break the synchronization semantics.
2. The connection health check (is the cached connection alive and not goaway'd?) must remain synchronous — do not introduce await points in the fast path.
3. If the method currently returns `Task.FromResult(connection)` for the fast path, this is the exact pattern that `ValueTask` eliminates.
4. Profile expected fast-path hit rate: ~90% for warm hosts with persistent connections.

---

## Step 2: Migrate TcpConnectionPool

Required behavior:

1. Migrate `TcpConnectionPool.GetConnectionAsync` to return `ValueTask<ConnectionLease>`.
2. The synchronous fast path (idle connection available in the pool) must return `new ValueTask<ConnectionLease>(lease)` directly.
3. The async slow path (need to establish new TCP connection) uses `async ValueTask<ConnectionLease>`.
4. Migrate `ReturnConnection` and any other async pool management methods to `ValueTask` where applicable.
5. Update all call sites — primarily the HTTP/1.1 transport and the HTTP/2 connection manager's underlying TCP acquisition.

Implementation constraints:

1. Connection pool access is often synchronized — ensure `ValueTask` doesn't leak across synchronization boundaries (e.g., don't return a `ValueTask` from inside a `SemaphoreSlim.WaitAsync` scope and expect the caller to await it after release).
2. Idle connection validation (is the socket still healthy?) may involve a non-blocking socket poll — this is synchronous and should remain in the fast path.
3. Connection lease disposal (`IDisposable.Dispose`) returns the connection to the pool — this path is synchronous and unaffected by `ValueTask` migration.

---

## Step 3: Migrate Transport Send/Receive Paths

Required behavior:

1. Migrate `IHttpTransport.SendAsync` implementations (already done in 19.1 at the interface level — this step covers the internal implementation details).
2. Migrate HTTP/1.1 transport internal methods: request serialization write path, response read path.
3. Migrate HTTP/2 transport internal methods: frame send path, stream data receive path.
4. Ensure the full request lifecycle (acquire connection → send request → read response → return connection) uses `ValueTask` end-to-end.

Implementation constraints:

1. Transport I/O methods (stream read/write) are almost always truly async — `ValueTask` provides minimal benefit here since the state machine allocation is dwarfed by I/O time. Migrate for consistency, not performance.
2. Response header parsing may have synchronous fast paths (headers already buffered) — these benefit from `ValueTask`.
3. Do not change the semantics of cancellation or timeout handling during this migration.
4. Ensure `Stream.ReadAsync` / `Stream.WriteAsync` overloads that accept `Memory<byte>` are used where possible — these return `ValueTask<int>` natively.

---

## Step 4: Verify Pipeline End-to-End ValueTask Flow

Required behavior:

1. Trace the complete request lifecycle from `UHttpClient.SendAsync` through middleware chain, pipeline, transport, connection pool, and back — verify `ValueTask` flows without any `Task` conversion at any stage.
2. Identify and remove any remaining `.AsTask()` calls that were added as temporary bridges during migration.
3. Verify that middleware `next` delegate invocations produce `ValueTask` results that are directly awaited (not stored).
4. Confirm connection pool acquisition → request → response → connection return all use `ValueTask` internally.

Implementation constraints:

1. Use a debugger or add temporary logging to trace a single request through the full pipeline — verify no `Task` boxing occurs.
2. Remove any defensive `.AsTask()` wrappers that are no longer needed.
3. The only remaining `.AsTask()` calls should be at boundaries where `Task` combinators are required (e.g., `Task.WhenAll` for concurrent requests) — document these exceptions.

---

## Verification Criteria

1. `Http2ConnectionManager.GetOrCreateAsync` returns `ValueTask<Http2Connection>` — fast path (cached connection) completes synchronously without allocation.
2. `TcpConnectionPool.GetConnectionAsync` returns `ValueTask<ConnectionLease>` — fast path (idle pooled connection) completes synchronously without allocation.
3. Full request lifecycle uses `ValueTask` end-to-end with no `Task` conversion in the hot path.
4. All existing transport and connection pool tests pass.
5. No `.AsTask()` calls remain in the hot path (middleware chain, transport send/receive).
6. Benchmark confirmation: warm HTTP/2 request to a cached host shows zero `Task<T>` allocations in the pipeline/transport layer.
