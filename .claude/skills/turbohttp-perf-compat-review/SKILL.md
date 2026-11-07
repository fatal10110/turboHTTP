---
name: turbohttp-perf-compat-review
description: >
  Performance and compatibility pass over a fatal10110/turboHTTP diff: hot-path allocations,
  copies, boxing and async state-machine cost, lock contention, algorithmic and memory growth,
  pool sizing; and compatibility with Unity 2021.3 / .NET Standard 2.1 / C# 9, IL2CPP/AOT and
  managed stripping, iOS/Android/32-bit targets, the customer-facing public API (source and binary
  breaks) and interop with real peers. Produces gated findings for the PR review. Use when
  turbohttp-pr-review section 5 or a turbohttp-waves review prompt names this skill, or when the
  user asks for a performance or compatibility review of a turboHTTP change. It is a review pass,
  not a full review: the bottom line, acceptance criteria and submission stay with
  turbohttp-pr-review.
---

# turboHTTP performance and compatibility review

TurboHTTP sells on two promises: it is cheap per request on a mobile device, and it runs unchanged
on every Unity 2021.3 target customers ship (Editor/Mono, Standalone, iOS and Android under IL2CPP).
This pass checks a diff against both. It runs after the correctness passes of `turbohttp-pr-review`
and feeds its findings into that skill's section 6 gate (same tags, same evidence bar). A finding
here needs the same four parts: where, failure scenario, evidence, fix. "Could be faster" without
a hot path, a size or a measurement is not a finding.

## 0. Scope the pass

List the changed members and classify each before reviewing:

- **Hot** — runs per byte, per frame, per header, per read/write or per request on the transport,
  HTTP/1.1 parser/serializer, HTTP/2 read loop/writer/HPACK, WebSocket framing/masking, TLS stream
  wrappers, pooling (`TurboHTTP.Core.Internal`), the pipeline/interceptor chain, response body
  buffering and JSON (de)serialization of bodies.
- **Warm** — per connection or per redirect/retry/auth round (connect, handshake, pool lease,
  cache lookup).
- **Cold** — configuration, startup, editor tooling, diagnostics that are off by default.

Spend the pass on hot members first. A cold member gets only the compatibility checks (§2).

## 1. Performance

Work each item against the code; name the line and the per-call cost.

- **Allocations on hot paths.** Code that was allocation-free stays so (CLAUDE.md §4). Look for:
  `new byte[]`/`new T[]` per call instead of `ArrayPool`/`ByteArrayPool`; `ToArray()`,
  `string.Concat`/interpolation/`Encoding.GetString` per header or frame; LINQ, lambdas capturing
  locals, `params` arrays, `foreach` over an interface (boxed enumerator), struct-to-interface or
  enum `HasFlag`/`ToString` boxing; `Task<T>` where an existing `ValueTask<T>` signature returns
  synchronously; `ct.Register` with a capturing lambda instead of
  `Register(static cb, state, useSynchronizationContext: false)`; exceptions used for control flow
  on expected paths.
- **Async cost.** Hot `ReadAsync`/`WriteAsync` paths complete synchronously when data is buffered
  (platform.md); a new `await` on a buffered path boxes the state machine. `ConfigureAwait(false)`
  on every library `await`; no `Task.Yield()` hops onto the Unity main thread (#245).
- **Copies.** Each extra pass over a body or frame payload (copy into a temp buffer, flatten a
  `SegmentedBuffer` via `UHttpResponse.Body` where `BodySequence` would do, `MemoryStream`
  round trips) is O(body) work and memory. Prefer `Span<T>`/`Memory<T>`/`ReadOnlySequence<byte>`
  over materializing.
- **Locks and contention.** No `await` or I/O under a lock; critical sections on the read loop or
  `_writeLock` stay short; no new lock on a path that `Interlocked`/single-owner handled; no
  per-request global lock (pool, DNS cache, limiter) without a reason.
- **Growth and complexity.** Per-request or per-connection collections bounded and cleaned on every
  path (dictionaries of streams, pending pings, cache entries, cookie jars, timers). No O(n²) over
  headers, streams or segments (repeated `List.Remove`, `IndexOf` in loops, string building by
  concatenation). Every peer-controlled size bounded before allocation (CLAUDE.md §3) — a missing
  bound is also a security finding.
- **Pools.** Rented sizes match need (no 1 MB rent for a 9-byte frame header); returned once on
  every path; pool caps keep idle memory bounded on mobile; no pool for objects that are cheaper to
  allocate.
- **Logging and diagnostics.** No string formatting or allocation when the log level or monitor is
  off (guard before building the message); redaction stays on.
- **Timers and wake-ups.** No per-request `Timer`/`Task.Delay` where a shared deadline or the
  transport cancellation exists; no polling loops; no busy-waits.

**Evidence.** Prefer measurement to argument:

- Allocation gates: `Tests/Runtime/Performance/StreamingAllocationGateTests.cs`,
  `Phase19AllocationGateTests.cs`, baselines in `Tests/Benchmarks/phase19-allocation-baselines.json`.
  A diff that touches a gated path runs the gate scoped:
  `'./Tools~/unity/run-tests.sh' --platform EditMode --filter 'AllocationGate'` (exit 3 = Unity not
  installed; record "not run"). A baseline raised in the diff needs a stated reason in the PR.
- A new hot path that is not covered by a gate is a `[should-fix]` asking for a gate test, or a
  `[note]` with a follow-up issue when the gate harness cannot reach it yet.
- Benchmarks in `Tests/Runtime/Performance/BenchmarkTests.cs` are `[Explicit]`; cite them only
  when you ran them.

**Severity.** A regression on a hot path that was allocation-free, an unbounded growth, or O(n²)
over peer-controlled input is `[must-fix]` (unbounded peer-driven growth is `[blocker]`, CLAUDE.md
§3). A warm-path allocation or a missed `Span` is at most `[should-fix]`. Micro-optimizations
without a hot path are not findings.

**Effort is never a reason to drop a real gain.** When a real performance gain (a hot path, with a
measured or clearly stated per-call/per-byte cost) needs extra work — a refactor across several
types, a custom implementation in place of a BCL or Unity API that allocates or copies (own parser,
pool, buffer, encoder, collection), or a change of internal API shape — it is either done or tracked:

- **In this PR** when it fits the PR's scope: post it as a finding with the tag its impact earns
  and describe the refactor or custom implementation in the fix line.
- **As a follow-up issue** otherwise: file it (or amend an existing one) in the owning milestone
  before the review is submitted — creating follow-ups is pre-authorized (CLAUDE.md §7). The issue
  names the hot path (path:line), the cost now (allocation-gate numbers, bytes or allocations per
  call, complexity), the proposed design, the compatibility constraints from §2 it must respect, and
  how to verify the gain (which gate or benchmark). Search for a duplicate first
  (`Tools~/agent-tools/issue_search.py`). The review lists it as `[note]` with the issue number.

"Too much work", "would need a custom implementation" or "out of scope" without an issue number is
not an acceptable disposition. The same applies to a compatibility fix that needs a larger change
(for example, replacing an API that IL2CPP or netstandard2.1 lacks with an in-house implementation).

## 2. Compatibility

### Toolchain: Unity 2021.3, .NET Standard 2.1, C# 9

The shadow build (`'./Tools~/ci/check.sh'`) mirrors this setup and catches most API misses; still
read for what it cannot see:

- No C# 10+ syntax: file-scoped namespaces, global usings, `record struct`, raw string literals,
  `required` members, list patterns, generic attributes, lambda natural types/attributes.
- `init` accessors and positional `record`s need `System.Runtime.CompilerServices.IsExternalInit`,
  which Unity 2021.3 does not ship and the runtime assemblies do not define today: avoid them.
- No .NET 5+ APIs: `ArgumentNullException.ThrowIfNull`, `Socket.ConnectAsync(..., CancellationToken)`,
  `Stream.ReadExactly`, `Random.Shared`, `Convert.ToHexString`, `SslClientHelloInfo`, and `Span`
  overloads newer than netstandard2.1. When unsure, check the netstandard2.1 reference assembly the
  shadow build uses, not docs for current .NET.
- Mono (Editor, Standalone Mono) and IL2CPP differ: a change that only ran in the Editor is not
  verified for IL2CPP. Mark device-only behavior `needs-device-validation` (CLAUDE.md §5).

### IL2CPP / AOT / stripping

- Banned APIs (`Tools~/ci/BannedSymbols.txt`) fail the build; also look for indirect runtime codegen:
  `dynamic`, `Type.GetType(string)` + `Activator`, reflection over members only reached by name,
  `Enum.GetValues` on hot paths, serializers that reflect over customer types.
- Generic virtual methods and new generic instantiations over value types (`ValueTask<TStruct>`,
  `Channel<T>`, `ConcurrentDictionary<TStructKey, ...>`) can lack AOT code: they need a direct
  closed use in the assembly or a device check (platform.md; link.xml in a UPM package is unreliable,
  #8). Anything reached only by reflection carries `[Preserve]` and survives stripping level High.
- No `[ModuleInitializer]`; registration goes through `TurboHttpBootstrap`.
- No `goto` across variable declarations in loops; `Math.Abs(int.MinValue)` throws (use
  `& int.MaxValue`).

### Platforms and memory model

- ARM64 and 32-bit IL2CPP (armv7 Android): shared fields go through `Volatile`/`Interlocked`/locks;
  64-bit fields read across threads use `Interlocked.Read` or a lock (torn reads on 32-bit); `int`
  flags, not `bool`, with `Interlocked`; doubles stored as `long` bits.
- iOS/Android networking: raw sockets bypass ATS and `NetworkSecurityConfig`, so the cleartext policy
  is ours (#85); IPv6-only/NAT64 networks (no IPv4 literals, use resolved addresses); app suspension
  and network changes kill sockets; DNS and `ConnectAsync` have no cancellation on netstandard2.1.
- WebGL is unsupported by the socket transport: a change must not make a module that is supposed to
  compile on WebGL (Core, JSON) reference socket types.
- Unity APIs only on the main thread; never block it.

### Customer compatibility (commercial package)

- **Public API.** Removing or renaming a public/protected member, changing a signature, a default
  parameter value (baked into callers at compile time), a return type, `sealed`/`virtual`, or adding
  a member to a public interface (breaks every customer implementation) is a breaking change: it needs
  the issue's or milestone's explicit approval and a PR mention; otherwise `[blocker]`. Prefer
  additive overloads, internal types and extension methods in the owning module (CLAUDE.md §4).
- **Behavior defaults.** A changed default (timeout, redirect limit, cookie/caching policy, TLS
  provider, HTTP/2 enablement, buffer limit) changes every customer's traffic; it needs a reason in
  the issue and a mention in the PR and docs (`Documentation~/`, `CHANGELOG.md`).
- **Assembly layout.** Renamed or moved asmdefs, namespaces or `versionDefines` break customer
  references and defines; module dependencies follow architecture.md.
- **Samples and docs.** A public API change updates `Samples~/` and `Documentation~/` in the same PR.

### Wire interoperability

- The RFC is the oracle (CLAUDE.md §2), and real peers vary: HTTP/1.0 responses, servers that close
  without `Content-Length`, HTTP/2 servers with small `SETTINGS_MAX_FRAME_SIZE`/header-list limits or
  no server push, proxies that downgrade, WebSocket servers without permessage-deflate. A change that
  is RFC-correct but rejects a common lenient case the code accepted before needs a reason; a change
  that makes us send something a strict peer rejects is a `[must-fix]`.

## 3. Output

Hand every finding that passes the gate to `turbohttp-pr-review` section 6 (tag first, path:line,
scenario, evidence, fix). Add this section to the review body, after "Specialist rubrics":

```markdown
## Performance and compatibility
Hot members reviewed: <list, or "none touched">.
Performance: <clean | items>. Evidence: <allocation gate filter and result | "Unity not run" | reasoning with path:line>.
Compatibility: <clean | items>. Toolchain/IL2CPP/platform/public API/wire: <one line each, or "n/a">.
Needs device validation: <items | none>.
Follow-ups filed or amended: <#issue — one line each | none>.
```

In a delta re-review, re-run only the checks the delta reaches; say so in the section.
