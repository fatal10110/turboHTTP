# Phase 4.1: Pipeline Executor + UHttpClient Integration

**Depends on:** Phase 3 (complete)
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new, 1 modified

---

## Pre-Step: Directory Cleanup

Before creating any files:

```bash
# Delete empty placeholder directory (no .asmdef, no code)
rm -rf Runtime/Pipeline/

# Create correct directory structure inside Core assembly
mkdir -p Runtime/Core/Pipeline/Middlewares
```

**Why:** The `Runtime/Pipeline/` directory was a Phase 1 placeholder. Core middlewares must live inside `TurboHTTP.Core` assembly (which is `autoReferenced: true`). A separate assembly would force users to add an extra reference.

---

## Step 1: `HttpPipeline` Class

**File:** `Runtime/Core/Pipeline/HttpPipeline.cs`
**Namespace:** `TurboHTTP.Core`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Executes a chain of middleware followed by the transport layer.
    /// The delegate chain is built once at construction and reused across requests.
    /// </summary>
    public class HttpPipeline
    {
        private readonly IReadOnlyList<IHttpMiddleware> _middlewares;
        private readonly IHttpTransport _transport;
        private readonly HttpPipelineDelegate _pipeline;

        public HttpPipeline(IEnumerable<IHttpMiddleware> middlewares, IHttpTransport transport)
        {
            _middlewares = middlewares?.ToList() ?? new List<IHttpMiddleware>();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _pipeline = BuildPipeline();
        }

        /// <summary>
        /// Execute the pipeline for a given request.
        /// </summary>
        public Task<UHttpResponse> ExecuteAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return _pipeline(request, context, cancellationToken);
        }

        private HttpPipelineDelegate BuildPipeline()
        {
            // Start with the transport as the final step
            HttpPipelineDelegate pipeline = (req, ctx, ct) =>
                _transport.SendAsync(req, ctx, ct);

            // Wrap each middleware in reverse order so they execute in list order:
            // Request flow:  M[0] → M[1] → M[2] → Transport
            // Response flow: Transport → M[2] → M[1] → M[0]
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var next = pipeline;

                pipeline = (req, ctx, ct) =>
                    middleware.InvokeAsync(req, ctx, next, ct);
            }

            return pipeline;
        }
    }
}
```

### Implementation Notes

1. **Delegate chain built once:** `BuildPipeline()` runs in the constructor. The resulting `HttpPipelineDelegate` is a closure chain — each lambda captures its middleware instance and the `next` delegate. This is O(n) construction, O(1) per-request overhead (just delegate invocation).

2. **No LINQ in hot path:** `ToList()` happens once in constructor. `ExecuteAsync()` is allocation-free (delegate invocation only).

3. **Empty middleware list:** When `_middlewares` is empty, `BuildPipeline()` returns the transport delegate directly — zero overhead vs calling `_transport.SendAsync()` directly.

4. **Thread safety:** The pipeline delegate chain is immutable after construction. Multiple threads can call `ExecuteAsync()` concurrently without synchronization (assuming middleware implementations are thread-safe).

---

## Step 2: Integrate Pipeline into `UHttpClient`

**File:** `Runtime/Core/UHttpClient.cs` (modify existing)
**Namespace:** `TurboHTTP.Core`

### Changes

**Add field** (after `_ownsTransport`):

```csharp
private readonly HttpPipeline _pipeline;
```

**Modify constructor** — add pipeline construction as the last line, after the existing transport initialization (which now has 3 branches for Transport/TlsBackend/Default):

```csharp
public UHttpClient(UHttpClientOptions options = null)
{
    _options = options?.Clone() ?? new UHttpClientOptions();

    if (_options.Transport != null)
    {
        _transport = _options.Transport;
        _ownsTransport = _options.DisposeTransport;
    }
    else if (_options.TlsBackend != TlsBackend.Auto)
    {
        _transport = HttpTransportFactory.CreateWithBackend(_options.TlsBackend);
        _ownsTransport = true;
    }
    else
    {
        _transport = HttpTransportFactory.Default;
        _ownsTransport = false;
    }

    _pipeline = new HttpPipeline(_options.Middlewares, _transport);
}
```

**Modify `SendAsync`** — replace `_transport.SendAsync(...)` with `_pipeline.ExecuteAsync(...)`:

```csharp
public async Task<UHttpResponse> SendAsync(
    UHttpRequest request, CancellationToken cancellationToken = default)
{
    if (request == null) throw new ArgumentNullException(nameof(request));
    ThrowIfDisposed();

    var context = new RequestContext(request);
    context.RecordEvent("RequestStart");

    try
    {
        // Execute through middleware pipeline (includes transport as final step)
        var response = await _pipeline.ExecuteAsync(request, context, cancellationToken);

        context.RecordEvent("RequestComplete");
        context.Stop();

        return response;
    }
    catch (UHttpException)
    {
        context.RecordEvent("RequestFailed");
        context.Stop();
        throw;
    }
    catch (OperationCanceledException)
    {
        context.RecordEvent("RequestCancelled");
        context.Stop();
        throw;
    }
    catch (Exception ex)
    {
        context.RecordEvent("RequestFailed");
        context.Stop();
        throw new UHttpException(
            new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
    }
}
```

### What Changes vs Current Code

Only ONE line changes in `SendAsync`:
```diff
- var response = await _transport.SendAsync(request, context, cancellationToken);
+ var response = await _pipeline.ExecuteAsync(request, context, cancellationToken);
```

Plus one line added at the end of the constructor (after the existing 3-branch transport init):
```diff
+ _pipeline = new HttpPipeline(_options.Middlewares, _transport);
```

And the new field:
```diff
  private readonly bool _ownsTransport;
+ private readonly HttpPipeline _pipeline;
```

### Backwards Compatibility

- **Zero middlewares (default):** `UHttpClientOptions.Middlewares` defaults to empty list. `HttpPipeline` with no middlewares delegates directly to transport. Behavior is identical to pre-Phase-4.
- **No API changes:** `UHttpClient` public API is unchanged. Users who don't configure middlewares see no difference.
- **Error handling unchanged:** All catch blocks remain identical. Middleware exceptions bubble up through the same handlers.

---

## Verification Criteria

- [ ] `HttpPipeline` compiles and is in `TurboHTTP.Core` namespace
- [ ] `UHttpClient` constructor creates pipeline from options
- [ ] `UHttpClient.SendAsync` uses `_pipeline.ExecuteAsync` instead of `_transport.SendAsync`
- [ ] Empty middleware list = same behavior as direct transport call
- [ ] `Runtime/Pipeline/` directory deleted
- [ ] `Runtime/Core/Pipeline/` directory created
