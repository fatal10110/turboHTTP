## 22.2: Transport Adaptation

### Transitional Bridge Removal

Phase 22.1 introduced a transitional bridge where `RawSocketTransport.DispatchAsync` delegates to the legacy `SendAsync` (returning `UHttpResponse`) and re-emits callbacks via `DispatchBridge.DeliverResponse`. Phase 22.2 removes this bridge entirely:

- **Delete** the legacy `SendAsync(UHttpRequest, RequestContext, CancellationToken)` method (the `ValueTask<UHttpResponse>` overload) from `RawSocketTransport`
- **Delete** all `DispatchBridge.DeliverResponse` calls from the transport — callbacks are driven directly
- **Delete** `response?.Dispose()` from the `DispatchAsync` finally block — no intermediate `UHttpResponse` exists
- **Simplify `ResponseCollectorHandler`**: remove `TrySetStoredCancellationException()` and the `DispatchBridge.ResponseErrorStateKey` / `CancellationExceptionStateKey` state-key reads. Error/cancellation context is now delivered directly through handler callbacks and Task faults, not via context state. `Fail()` becomes a straightforward TCS fault, `Cancel()` becomes a straightforward TCS cancel.
- **Delete `DispatchBridge.DeliverResponse`** if no other callers remain (check `MockTransport` and `RecordReplayTransport` — they drive callbacks directly and should not need it). Retain `DispatchBridge.CollectResponseAsync` and `AttachCompletion` if still used by `UHttpClient.SendAsync`.

### `Runtime/Transport/RawSocketTransport.cs`

`DispatchAsync(request, handler, context, ct)` now drives handler callbacks directly (no delegation to legacy `SendAsync`).

Add private helper:
```csharp
private static UHttpException MapException(Exception ex) =>
    ex is UHttpException u ? u :
    new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message), ex);
```

All timeout CTS, proxy resolution, URI validation unchanged. Per the **Error Delivery Contract**: catch blocks that previously `throw` now call `handler.OnResponseError(MapException(ex), context)` and `return` — Task completes normally, error delivered via handler. Preserve `RequestFailed` / `RequestCancelled` `context` events inside the catch blocks **before** calling `OnResponseError`.

**C-18 ownership fix:** remove **all** transport-level `request.DisposeBodyOwner()` calls from `RawSocketTransport` and `Http2Connection`. Request body lifetime remains owned by `UHttpRequest` and is released only when the outermost request lifecycle ends (`Dispose`, `ResetForPool`, or equivalent outer executor cleanup). Re-dispatching interceptors must be able to send the same request body again safely.

**HTTP/1.1 path:**
```csharp
handler.OnRequestStart(request, context);   // fires BEFORE network I/O (H-6)
try
{
    var parsed = await SendOnLeaseAsync(request, ...).ConfigureAwait(false);
    handler.OnResponseStart(parsed.StatusCode, parsed.Headers, context);
    foreach (var segment in parsed.EnumerateBodySegments())
        handler.OnResponseData(segment.Span, context);
    handler.OnResponseEnd(HttpHeaders.Empty, context);
    // parsed pool return happens here — after all callbacks
}
catch (Exception ex) when (!(ex is OperationCanceledException))
{
    context.RecordEvent("RequestFailed");
    handler.OnResponseError(MapException(ex), context);
    return; // Task completes normally
}
```

**HTTP/2 path** — `h2Conn.DispatchAsync(request, handler, context, ct)` (awaits stream completion, no return value). `OnRequestStart` is called by the transport BEFORE handing off to `Http2Connection.DispatchAsync`. As with HTTP/1.1, request-body ownership remains on `UHttpRequest`; `Http2Connection` must not dispose the body owner in its outer `finally`.

**`H2Manager`** — unchanged; `h2Conn.SendRequestAsync` → `h2Conn.DispatchAsync`.

**Stale retry path** — retry calls `h2Conn.DispatchAsync` / `SendOnLeaseAsync` with the same `handler` (handler is reentrant here; no callbacks fired yet on the failed attempt).

### `Runtime/Transport/Http1/Http11ResponseParser.cs`

`SegmentedBuffer` does not have `AsMemorySegments()`. Use `AsSequence()` (H-5):

```csharp
// Yields body as ReadOnlyMemory<byte> segments without copying
internal IEnumerable<ReadOnlyMemory<byte>> EnumerateBodySegments()
{
    if (_segmentedBody != null)
    {
        foreach (var mem in _segmentedBody.AsSequence())
            yield return mem;
    }
    else if (!_body.IsEmpty)
    {
        yield return _body; // single ArrayPool segment
    }
}
```

No changes to parsing logic.

### `Runtime/Transport/Http2/Http2Stream.cs`

**Remove:**
- `private MemoryStream _responseBodyStream`
- `private PoolableValueTaskSource<UHttpResponse> _responseSource` (Phase 19a.5 pool)
- `public ValueTask<UHttpResponse> ResponseTask`
- Body accumulation logic inside `AppendResponseData`

**Add:**
- `private IHttpHandler _handler`
- `private HttpHeaders _trailers` (H-4: trailing headers accumulate separately)
- Zero-alloc completion source (C-6 — replace `new TaskCompletionSource(...)` to avoid per-request allocation and the non-generic TCS .NET 5+ dependency):

```csharp
// Http2Stream implements IValueTaskSource to embed the source inline — no per-use allocation
private ManualResetValueTaskSourceCore<VoidResult> _completionSource; // struct, embedded
public ValueTask CompletionTask => new ValueTask(this, _completionSource.Version);
// Implement IValueTaskSource<VoidResult> methods delegating to _completionSource
```

**Update:**
- `HandleResponseHeaders(int statusCode, HttpHeaders headers)` → `_handler.OnResponseStart(statusCode, headers, _context)`
- `AppendResponseData(byte[] payload, int offset, int length)` → `_handler.OnResponseData(new ReadOnlySpan<byte>(payload, offset, length), _context)` — keep `ObjectDisposedException` guard
- `AppendTrailers(HttpHeaders trailers)` → `_trailers = trailers` (called by read loop when `isTrailingHeaders: true` — H-4)
- `Complete()`:
  ```csharp
  _handler.OnResponseEnd(_trailers ?? HttpHeaders.Empty, _context);
  _completionSource.SetResult(default);
  ```
- `Cancel()`:
  ```csharp
  _completionSource.SetException(new OperationCanceledException());
  ```
  > `Cancel()` sits below the handler contract boundary. It does **not** call `_handler.OnResponseError`; `Http2Connection.DispatchAsync` / `RawSocketTransport.DispatchAsync` enforce the contract by propagating `OperationCanceledException` upward, recording `RequestCancelled`, and delivering no response-error callback.
- `PrepareForPool()`: `_handler = NullHandler.Instance`; `_trailers = null`; `_completionSource.Reset()` (in-place reset, zero allocation — C-6 fix)

**`NullHandler`** (`internal static` class at transport assembly level, e.g., `Runtime/Transport/NullHandler.cs`): no-op `IHttpHandler` — guards against read loop race on pooled stream return. Scoped `internal` rather than `private` to `Http2Stream` so other transport code (e.g., pooled connection health checks) can reuse it without duplicating the implementation.

### `Runtime/Transport/Http2/Http2StreamPool.cs`

- Remove `sourcePool: PoolableValueTaskSource<UHttpResponse>[]` from `Rent` signature (Phase 19a.5 pool removed for stream completion)
- Add `handler: IHttpHandler` parameter
- `Rent(streamId, request, handler, context, remoteWindow, localWindow)` sets `stream._handler = handler`

### `Runtime/Transport/Http2/Http2Connection.cs`

- Rename `SendRequestAsync` → `DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context, CancellationToken ct)`
- Pass `handler` to `Http2StreamPool.Rent`
- Replace `return await stream.ResponseTask` with `await stream.CompletionTask`
- Return type: `ValueTask<UHttpResponse>` → `Task`
- Cancellation path: `OperationCanceledException` propagates out of `DispatchAsync` with no `handler.OnResponseError`; non-cancellation transport failures are mapped to `handler.OnResponseError(...)` and complete normally.

### `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`

**This sub-section has behavioral changes — the plan originally stated "no method signature changes" which was incorrect (H-9).**

`DecodeAndSetHeaders` currently assigns `stream.ResponseHeaders = responseHeaders` and `stream.StatusCode = statusCode` directly. These direct field assignments are replaced:

| Old (field assignment) | New (handler dispatch) |
|---|---|
| `stream.StatusCode = statusCode` | `stream.HandleResponseHeaders(statusCode, headers)` → calls `_handler.OnResponseStart(...)` |
| `stream.ResponseHeaders = headers` | (folded into `HandleResponseHeaders`) |
| `// trailing: merge into ResponseHeaders` | `stream.AppendTrailers(trailingHeaders)` → sets `stream._trailers` (H-4) |

Specify the exact change in `DecodeAndSetHeaders` result handling:
```csharp
// After decoding headers from HPACK:
if (isTrailingHeaders)
    stream.AppendTrailers(decodedHeaders);
else
    stream.HandleResponseHeaders(statusCode, decodedHeaders);
```

All other read loop logic (frame parsing, flow control, GOAWAY, RST_STREAM) unchanged.

### Validation
- **Bridge removal confirmed:** no `DispatchBridge.DeliverResponse` calls remain in transport code; no intermediate `UHttpResponse` allocation on the hot path; `ResponseCollectorHandler` has no `DispatchBridge` state-key reads; `grep -r "DeliverResponse\|ResponseErrorStateKey\|CancellationExceptionStateKey" Runtime/Transport/` returns zero matches
- `IntegrationTests` deterministic suite passes (HTTP/1.1 + HTTP/2)
- `Http2ConnectionTests` pass
- `Http2FlowControlTests` pass
- `StressTests` (1000-request, concurrency enforcement, multi-host) pass
- Retry / redirect re-dispatch of a pooled-body request succeeds without use-after-free or pool corruption
- Cancellation path validated: `Http2Stream.Cancel()` causes `OperationCanceledException` and **no** `OnResponseError`
