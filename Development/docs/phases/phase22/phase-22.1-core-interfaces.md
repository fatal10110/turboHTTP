## 22.1: Core Interfaces & Pipeline

### Files Removed
- `Runtime/Core/IHttpMiddleware.cs`
- `Runtime/Core/Pipeline/HttpPipeline.cs`

### Files Created

**`Runtime/Core/IHttpHandler.cs`** — interface as above (with XML docs); namespace `TurboHTTP.Core`

**`Runtime/Core/IHttpInterceptor.cs`** — interface as above; namespace `TurboHTTP.Core`

**`Runtime/Core/DispatchFunc.cs`** — replaces `HttpPipelineDelegate`:
```csharp
namespace TurboHTTP.Core
{
    public delegate Task DispatchFunc(
        UHttpRequest request,
        IHttpHandler handler,
        RequestContext context,
        CancellationToken ct);
}
```

**`Runtime/Core/Pipeline/InterceptorPipeline.cs`** — right-fold over interceptors, terminal node wraps transport:
```csharp
public sealed class InterceptorPipeline
{
    private readonly DispatchFunc _pipeline;

    public InterceptorPipeline(IReadOnlyList<IHttpInterceptor> interceptors, IHttpTransport transport)
    {
        if (interceptors == null) throw new ArgumentNullException(nameof(interceptors)); // L-2
        if (transport == null) throw new ArgumentNullException(nameof(transport));

        DispatchFunc terminal = (req, handler, ctx, ct) =>
            transport.DispatchAsync(req, handler, ctx, ct);

        for (int i = interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = interceptors[i];
            var next = terminal;
            terminal = interceptor.Wrap(next);
        }
        _pipeline = terminal;
    }

    public DispatchFunc Pipeline => _pipeline;
}
```

**`Runtime/Core/Pipeline/ResponseCollectorHandler.cs`** — internal terminal handler:
```csharp
internal sealed class ResponseCollectorHandler : IHttpHandler
{
    private readonly TaskCompletionSource<UHttpResponse> _tcs =
        new TaskCompletionSource<UHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    private UHttpRequest _request;
    private readonly RequestContext _context;
    private int _statusCode;
    private HttpHeaders _responseHeaders;
    private SegmentedBuffer _body;

    internal ResponseCollectorHandler(UHttpRequest request, RequestContext context)
    {
        _request = request;
        _context = context;
        _body = new SegmentedBuffer();
    }

    public void OnRequestStart(UHttpRequest request, RequestContext context)
        => _request = request; // updated on redirect hops

    public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
    {
        _statusCode = statusCode;
        _responseHeaders = headers;
    }

    public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        => _body.Write(chunk);

    public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
    {
        var response = new UHttpResponse(
            _statusCode, _responseHeaders,
            _body.AsSequence(), _body,
            _context.Elapsed, _request);
        _tcs.TrySetResult(response);
    }

    public void OnResponseError(UHttpException error, RequestContext context)
    {
        _body?.Dispose();
        _tcs.TrySetException(error);
    }

    internal void Fail(Exception ex) =>
        _tcs.TrySetException(ex is UHttpException u ? u : new UHttpException(ex));

    internal void Cancel() =>
        _tcs.TrySetCanceled();

    /// <summary>
    /// Called when the pipeline Task completes normally without any handler callback
    /// having resolved the TCS. Faults the TCS to prevent SendAsync from hanging.
    /// </summary>
    internal void EnsureCompleted()
    {
        if (!_tcs.Task.IsCompleted)
            _tcs.TrySetException(new InvalidOperationException(
                "Pipeline completed without delivering a response."));
    }

    public Task<UHttpResponse> ResponseTask => _tcs.Task;
}
```

### Files Modified

**`Runtime/Core/IHttpTransport.cs`** — replace `SendAsync` with `DispatchAsync` as above.

**`Runtime/Core/HttpHeaders.cs`** — add static property (L-1):
```csharp
public static readonly HttpHeaders Empty = new HttpHeaders();
```

**`Runtime/Core/UHttpClientOptions.cs`**:
- `List<IHttpMiddleware> Middlewares` → `List<IHttpInterceptor> Interceptors`
- `Clone()`: shallow-copy interceptor list (`new List<IHttpInterceptor>(Interceptors)`)
- Update `Clone()` XML doc (M-8): "Stateful interceptors (`ConcurrencyInterceptor`, `CacheInterceptor`) are shared across clones — they hold per-client state and are intentionally singleton-per-`UHttpClient`."

**`Runtime/Core/UHttpClient.cs`**:
- **Return type of `SendAsync`**: `ValueTask<UHttpResponse>` → `Task<UHttpResponse>` (clean break — C-3b)
- Remove `_baseMiddlewares: IReadOnlyList<IHttpMiddleware>`, `HttpPipeline _pipeline`
- Add `DispatchFunc _pipeline` (volatile for plugin rebuild)
- Constructor: `_pipeline = new InterceptorPipeline(BuildInterceptors(_options), _transport).Pipeline`
- `SendAsync`: use `ResponseCollectorHandler` pattern with `EnsureCompleted` safety net and `RetainForResponse` wiring (see Architecture Overview above). Redirect handling keeps the outer `_pipeline(...)` Task pending across follow-up hops, so `EnsureCompleted()` remains a true bug trap rather than firing on normal redirect chains.
- `RebuildPipelineSnapshot_NoLock`: rebuild from `_baseInterceptors` + plugin contributions → `new InterceptorPipeline(...).Pipeline`
- `Dispose`: iterate `_options.Interceptors`, dispose `IDisposable` instances
- `PluginRegistration.Middlewares: IReadOnlyList<IHttpMiddleware>` → `Interceptors: IReadOnlyList<IHttpInterceptor>`
- `PluginContext.RegisterMiddleware` → `RegisterInterceptor`
- `BuildInterceptors` replaces `BuildPipelineMiddlewares`
- `MonitorInterceptorTypeName = "TurboHTTP.Observability.MonitorInterceptor, TurboHTTP.Observability"` — the `#if UNITY_EDITOR` block must use `typeof(IHttpInterceptor).IsAssignableFrom(monitorType)` (not `IHttpMiddleware`) (L-3)

**`Runtime/Core/UHttpRequest.cs`** — add `Clone()` for clone-on-write request mutation and background work. Phase 22 continues to use existing `SetTimeoutInternal(TimeSpan)` on cloned requests; no new fluent `WithTimeoutInternal()` API is introduced.

**`Runtime/Core/RequestContext.cs`** — add `CreateForBackground(UHttpRequest request)` to create a fresh context for cache revalidation / background replay work that must outlive the original `SendAsync` lifecycle.

**`Runtime/Core/IHttpPlugin.cs`** — add `PluginCapabilities.AllowRedispatch` so plugin enforcement can distinguish one downstream dispatch from multiple `next(...)` calls.

**`Runtime/Core/PluginContext.cs`** — `RegisterMiddleware(IHttpMiddleware)` → `RegisterInterceptor(IHttpInterceptor)`.

### New API Surface (H-11, H-13)

**`Runtime/Core/UHttpRequest.cs`**:
```csharp
public UHttpRequest Clone();
```

- Returns a detached copy of the request with cloned headers and metadata.
- The clone must be safe for redirect/cache/background flows: if the source uses pooled body storage, `Clone()` either retains safe shared ownership of the body backing store or falls back to copying the body bytes so the clone can outlive the original request.
- Interceptors that add transient headers/timeouts must clone first, mutate the clone, call `ctx.UpdateRequest(clone)`, and pass the clone downstream.

**Timeout mutation contract:**
- `internal void SetTimeoutInternal(TimeSpan timeout)` remains the internal mutator.
- Phase 22 does **not** add a fluent `WithTimeoutInternal()` method; `AdaptiveInterceptor` clones first, then calls `SetTimeoutInternal(...)` on the clone.

**`Runtime/Core/RequestContext.cs`**:
```csharp
internal static RequestContext CreateForBackground(UHttpRequest request);
```

- Returns a fresh context with its own stopwatch, timeline, and state bag.
- Used by cache stale-while-revalidate / other background dispatches so `UHttpClient.SendAsync` can safely clear the original foreground context without racing background work.

### `CapabilityEnforcedInterceptor` (C-4, M-14, M-17)

`PluginContext.CapabilityEnforcedMiddleware` (≈220 lines) must be redesigned — the enforcement mechanisms are incompatible with the interceptor model. **New design** for `CapabilityEnforcedInterceptor`:

| Capability check | Mechanism |
|--|--|
| Request replacement | Guarded `next` rejects passing a different request instance unless the plugin has `MutateRequests` |
| In-place request mutation | Compare `RequestMutationSignature` on the original request object before/after plugin execution and before entering downstream dispatch |
| Response mutation | Wrap `handler` with `MonitoringHandler`; detect unauthorized mutation via call sequence and data comparison |
| Re-dispatch guard | Wrap `next` with a counting guard: plugins without `AllowRedispatch` that call `next` more than once throw `PluginException` |
| Handler wrapping | The `MonitoringHandler` wrapper intercepts all callbacks to detect unauthorized out-of-band response data injection |

```csharp
internal sealed class CapabilityEnforcedInterceptor : IHttpInterceptor
{
    public DispatchFunc Wrap(DispatchFunc next) => async (req, handler, ctx, ct) =>
    {
        var originalRequest = req;
        var sigBefore = RequestMutationSignature.Capture(originalRequest);

        int dispatchCount = 0;
        DispatchFunc guarded = async (r, h, c, token) =>
        {
            if (!_caps.HasFlag(PluginCapabilities.MutateRequests) &&
                !ReferenceEquals(r, originalRequest))
            {
                throw new PluginException(
                    _pluginName,
                    "interceptor.request",
                    "Plugin interceptor attempted request replacement without MutateRequests capability.");
            }

            if (!_caps.HasFlag(PluginCapabilities.MutateRequests) &&
                !RequestMutationSignature.Capture(originalRequest).Equals(sigBefore))
            {
                throw new PluginException(
                    _pluginName,
                    "interceptor.request",
                    "Plugin interceptor attempted in-place request mutation without MutateRequests capability.");
            }

            if (++dispatchCount > 1 &&
                !_caps.HasFlag(PluginCapabilities.AllowRedispatch))
            {
                throw new PluginException(
                    _pluginName,
                    "interceptor.request",
                    "Plugin interceptor attempted re-dispatch without AllowRedispatch capability.");
            }

            await next(r, h, c, token).ConfigureAwait(false);
        };

        var monitored = new PluginMonitoringHandler(handler, _caps);
        await _pluginInterceptor.Wrap(guarded)(req, monitored, ctx, ct).ConfigureAwait(false);

        if (!_caps.HasFlag(PluginCapabilities.MutateRequests))
        {
            var sigAfter = RequestMutationSignature.Capture(originalRequest);
            if (!sigBefore.Equals(sigAfter))
            {
                throw new PluginException(
                    _pluginName,
                    "interceptor.request",
                    "Plugin interceptor attempted in-place request mutation without MutateRequests capability.");
            }
        }
    };
}
```

Clarification for M-14: the signature comparison intentionally targets **in-place mutation of the original request object**. Legitimate clone-on-write flows are governed by the explicit `ReferenceEquals` request-replacement guard above, not by the before/after signature comparison.

### Validation
- Core assembly compiles with zero references to `IHttpMiddleware` or `HttpPipeline`
- `new UHttpClient()` constructs without exception
- `new InterceptorPipeline(Array.Empty<IHttpInterceptor>(), mockTransport).Pipeline` is non-null
- `InterceptorPipeline(null, transport)` throws `ArgumentNullException`
- `CapabilityEnforcedInterceptor` rejects request replacement without `MutateRequests` and a second `next(...)` call without `AllowRedispatch`
