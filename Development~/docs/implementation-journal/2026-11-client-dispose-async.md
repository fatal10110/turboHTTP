# 2026-11: awaitable client disposal (#279)

Lane `client-dispose-async` of the w1110 waves run.

## What changed

- `UHttpClient` implements `IAsyncDisposable`. `DisposeAsync()` marks the client disposed, snapshots and
  clears the registered plugins under `_pluginLock` (same order as before, so the #178 re-check in
  `RegisterPluginAsync` still rejects late registrations), then awaits each plugin's `ShutdownAsync` in
  reverse registration order, one at a time. Only after every plugin completed, failed or timed out does it
  dispose the configured interceptors (when `DisposeInterceptors` is set, #63) and then the owned transport.
- Each plugin wait is bounded by `PluginShutdownTimeout`: the plugin's token is cancelled at the timeout
  (existing `ShutdownPluginOnDisposeAsync`), and `ShutdownPluginBoundedAsync` additionally stops waiting at
  the same timeout for a plugin that ignores its token (`Task.WhenAny` with a cancellable `Task.Delay`), so a
  misbehaving plugin cannot hang `DisposeAsync`. The abandoned shutdown task stays owned by
  `ShutdownPluginOnDisposeAsync`, which catches and logs every outcome.
- `Dispose()` keeps its non-blocking behaviour (no sync-over-async): it starts the plugin shutdowns without
  awaiting them and disposes the owned components at once. Its XML doc now says so and points to
  `DisposeAsync`. Both methods share one `BeginDispose()` (the single `Interlocked.CompareExchange` on
  `_disposed`), so whichever runs first disposes and every later call of either kind is a no-op.
- `UHttpClient` is now `sealed`, and the `GC.SuppressFinalize(this)` call (no finalizer exists) is gone
  (CA1063/CA1816). Nothing in the repository subclasses it.

## Files

- `Runtime/Core/UHttpClient.cs`: class declaration and doc, `Dispose()`, `DisposeAsync()`, `BeginDispose()`,
  `DisposeOwnedComponents()`, `DisposeAfterPluginShutdownAsync()`, `ShutdownPluginBoundedAsync()`.
- `Runtime/Core/UHttpClientOptions.cs`: `PluginShutdownTimeout` doc names both dispose paths.
- `Documentation~/APIReference.md`: `UHttpClient` section and method table.
- `Tests/Runtime/Core/UHttpClientDisposeAsyncTests.cs` (new): gated shutdown ordering, reverse sequential
  order of two plugins, timeout (one plugin honouring the token, one ignoring it), throwing shutdowns
  (inline and after a yield), `Dispose`/`DisposeAsync` idempotence in both orders and registration after
  `DisposeAsync` started.

## Decisions

- Sealed the class rather than adding a `Dispose(bool)` pattern: the client owns no unmanaged resource and
  has no finalizer, and no subclass exists in the repository. Public API change, noted in the PR.
- Plugins are shut down sequentially in reverse registration order (the order `Dispose()` starts them in), so
  a later plugin that depends on an earlier one finishes first. The worst-case `DisposeAsync` duration is
  therefore plugin count times `PluginShutdownTimeout`.
- `DisposeAsync` takes no `CancellationToken` (`IAsyncDisposable` has none); `PluginShutdownTimeout` is the
  only bound, as the issue specifies.
- A second `DisposeAsync` while the first is still running returns at once instead of awaiting the first one
  (documented). Interceptors and the transport stay `IDisposable`-only; awaiting `IAsyncDisposable`
  components was not asked for.
- When a cooperative plugin cancels at the timeout, its completion and the client's own bound fire at about
  the same time; if the bound wins, the client proceeds while the plugin's cancellation unwinds. The plugin
  has timed out by then, which is the condition the issue allows.

## Verification

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build with analyzers; no new warning).
- Unity EditMode scoped run: see the PR "Local gates" section.
- Not run: the full Unity suites (periodic host-editor run), PlayMode, IL2CPP players, devices. Nothing here is
  platform-specific.

## Deferred

None.
