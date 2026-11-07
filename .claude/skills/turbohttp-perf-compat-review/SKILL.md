---
name: turbohttp-perf-compat-review
description: >
  Performance and compatibility pass over a fatal10110/turboHTTP diff (hot-path cost; Unity 2021.3,
  .NET Standard 2.1, C# 9, IL2CPP, mobile, public API, wire interop). Run by turbohttp-pr-review §5
  and the turbohttp-waves reviewer, or when asked for a perf/compat review. Findings feed
  turbohttp-pr-review §6; it gives no verdict of its own.
---

# Performance and compatibility pass

Findings use the `turbohttp-pr-review` §6 gate: where, scenario, evidence, fix. "Could be faster"
without a hot path or a number is not a finding.

## Scope

Hot = per byte/frame/header/read/write/request (transport, HTTP/1.1, HTTP/2 + HPACK, WebSocket,
TLS wrappers, `Core.Internal` pools, pipeline, body buffering, JSON). Warm = per connection or
round trip. Cold = config, startup, editor. Review hot code first; cold code gets only §Compatibility.

## Performance

- Allocation-free paths stay so: no per-call arrays (use `ArrayPool`/`ByteArrayPool`), strings,
  LINQ, capturing lambdas, boxing, `Task<T>` where `ValueTask<T>` completes synchronously, capturing
  `ct.Register`, exceptions as control flow.
- No new `await` on buffered paths; `ConfigureAwait(false)`; no `Task.Yield()` onto the main thread.
- No extra passes over bodies/payloads; prefer `Span`/`ReadOnlySequence<byte>` to flattening.
- No `await` or I/O under a lock; no new global per-request lock.
- Collections bounded and cleaned on every path; no O(n²) over headers, streams or segments.
- Rent what is needed, return once; logging allocates nothing when off; no per-request timers or polling.

Evidence: the allocation gates (`Tests/Runtime/Performance/*AllocationGateTests.cs`, baselines in
`Tests/Benchmarks/`), run scoped with `--filter 'AllocationGate'`; a raised baseline needs a reason.
An ungated new hot path gets a gate test or a follow-up.

Severity: regression on an allocation-free hot path or O(n²) on peer input → `[must-fix]`;
unbounded peer-driven growth → `[blocker]`; warm-path cost → at most `[should-fix]`.

**Effort never drops a real gain.** If a measurable gain (or a compatibility fix) needs a refactor
or a custom implementation, raise it in this PR when in scope; otherwise file or amend a follow-up
issue in the owning milestone before submitting (hot path, current cost, proposed design, how to
verify) and list it as `[note] #N`. "Too much work" without an issue number is not a disposition.

## Compatibility

- **Toolchain:** C# 9 only (no file-scoped namespaces, global usings, raw strings, `required`,
  list patterns); no `init`/records (Unity lacks `IsExternalInit`); no .NET 5+ APIs (`ThrowIfNull`,
  cancellable `Socket.ConnectAsync`, `ReadExactly`, `Random.Shared`, `Convert.ToHexString`).
  The shadow build in `check.sh` is the reference.
- **IL2CPP:** banned APIs fail the build; also `dynamic`, reflection by name, new value-type generic
  instantiations (needs a device check), `[Preserve]` for reflection targets, no `[ModuleInitializer]`.
- **Platforms:** `Volatile`/`Interlocked` for shared fields, `Interlocked.Read` for 64-bit on 32-bit;
  cleartext policy is ours (#85); IPv6/NAT64; suspension; Unity APIs on the main thread only; no
  socket types in modules that must build on WebGL. Device-only claims → `needs-device-validation`.
- **Customers:** a public API break (signature, default parameter value, new interface member,
  `sealed`/`virtual`) or a changed behavior default needs explicit approval and a PR, docs and
  `CHANGELOG.md` mention, else `[blocker]`.
- **Wire:** RFC-correct but rejecting a common lenient peer needs a reason; sending what a strict
  peer rejects is `[must-fix]`.

## Output

Add to the review body (delta reviews: only what the delta reaches):

```markdown
## Performance and compatibility
Hot paths: <list | none>. Perf: <clean | items> (<gate result | Unity not run>).
Compat: <clean | items>. Device validation: <items | none>. Follow-ups: <#N | none>.
```
