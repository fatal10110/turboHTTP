# Phase 22b: Streaming Extensions — Overview

**Milestone:** M4 (v2.0 follow-up)
**Dependencies:** Phase 22a (end-to-end streaming must be complete)
**Estimated Complexity:** Medium-High
**Critical:** No — these are enhancements to the streaming substrate established in 22a, not blockers for core streaming functionality.
**Compatibility:** Additive. No breaking changes to the Phase 22a streaming API surface.

> **Source document:** The detailed plan lives at `Development/docs/phases/phase-22b-streaming-extensions.md`. This directory breaks the plan into per-sub-phase implementation files.

## Context

Phase 22a established the end-to-end streaming substrate: pull-based body sources (`IResponseBodySource`, `UHttpRequestBody`), dual buffered/streaming paths (`SendBufferedAsync`/`SendStreamingAsync`), bounded memory via per-stream queues and pooled transfer buffers, and the updated interceptor contract (`OnResponseStartAsync`). Four capabilities were explicitly deferred as non-goals of 22a:

1. **`Expect: 100-continue`** — avoid sending non-replayable streaming bodies to servers that will reject them
2. **Streaming through proxy connections** — `DispatchViaProxyAsync` still uses the full-buffered push-based path
3. **HTTP/1.1 response trailer parsing** — `GetTrailersAsync` returns `HttpHeaders.Empty` for HTTP/1.1
4. **HTTP/1.1 request trailers** — chunked encoding sends only `0\r\n\r\n` without actual trailer fields

Phase 22b picks up all four as a focused follow-on. Each is a self-contained sub-phase with its own deliverables and completion criteria.

## Sub-Phase Index

| Sub-Phase | Name | Effort | Depends On |
|-----------|------|--------|------------|
| [22b.1](phase-22b.1-expect-100-continue.md) | `Expect: 100-continue` Handling | 3–4 days | Phase 22a |
| [22b.2](phase-22b.2-proxy-streaming.md) | Streaming Through Proxy Connections | 2–3 days | Phase 22a (benefits from 22b.1) |
| [22b.3](phase-22b.3-response-trailers.md) | HTTP/1.1 Response Trailer Parsing | 2–3 days | Phase 22a |
| [22b.4](phase-22b.4-request-trailers.md) | HTTP/1.1 Request Trailer Support | 2–3 days | 22b.3 |

Total: 9–13 days

## Dependency Graph

```
Phase 22a (complete)
    │
    ├── 22b.1 Expect: 100-continue          ─── independent
    ├── 22b.2 Streaming Through Proxies      ─── independent (benefits from 22b.1)
    ├── 22b.3 Response Trailer Parsing       ─── independent
    └── 22b.4 Request Trailer Support        ─── depends on 22b.3
```

22b.1, 22b.2, and 22b.3 are independent and can be implemented in parallel. 22b.4 depends on 22b.3 (shared prohibited-trailer filtering logic).

## Recommended Implementation Order

```
22b.3 → 22b.4 → 22b.1 → 22b.2
```

Rationale:
1. **22b.3 first:** Response trailer parsing is the simplest and most self-contained. Establishes the prohibited-trailer filtering code reused by 22b.4.
2. **22b.4 second:** Request trailers build on 22b.3's filtering logic and complete the trailer story.
3. **22b.1 third:** 100-continue is the most complex sub-phase (serialization split, concurrent read/write, timer management). Having trailers done first avoids interleaving concerns.
4. **22b.2 last:** Proxy streaming depends on the 22a streaming dispatch path being stable and benefits from all other 22b features being available.

## Non-Goals

1. **HTTP/2 trailer changes.** HTTP/2 trailers already work via HEADERS frames with END_STREAM.
2. **SOCKS proxy support.** Only HTTP/HTTPS CONNECT proxy tunneling is in scope.
3. **Automatic trailer-based integrity verification.** Trailers are parsed and exposed; the client does not auto-validate.
4. **HTTP/2 `Expect: 100-continue` server push optimization.** Deferred.
5. **Proxy ALPN negotiation for HTTP/2.** Covered by Phase 22c.

## RFC Compliance Matrix

| RFC | Section | Feature | Sub-Phase |
|-----|---------|---------|-----------|
| RFC 9110 | 10.1.1 | `Expect: 100-continue` semantics | 22b.1 |
| RFC 9110 | 6.6.2 | Trailer header field, prohibited trailers | 22b.3, 22b.4 |
| RFC 9112 | 7.1.2 | HTTP/1.1 chunked trailer section | 22b.3, 22b.4 |
| RFC 9113 | 8.1 | HTTP/2 `100 Continue` in HEADERS frame | 22b.1 |
| RFC 9113 | 8.1 | HTTP/2 trailing HEADERS frame | 22b.4 |

## Cross-Cutting Test Requirements

- All new tests must run under Unity Test Runner with NUnit
- All new tests go in `Tests/Runtime/` under appropriate subdirectories
- `MockTransport` is used for unit tests, not real network I/O
- Integration tests use the `ExternalNetwork` test category for optional real-server testing
- IL2CPP-sensitive patterns (`ValueTask<T>`, `IAsyncDisposable`) must have `link.xml` entries
- Verify `TaskCompletionSource<HttpHeaders>` is not stripped by IL2CPP (22b.3)

## Platform Validation Requirements

The following must be validated on physical devices before Phase 22b is considered complete:

1. **iOS physical device (IL2CPP):** SslStream concurrent read+write during 100-continue wait (22b.1). The Apple Security framework TLS backend may behave differently from desktop .NET.
2. **Android physical device (IL2CPP):** Same concurrent read+write validation with OpenSSL-backed SslStream via Mono.
3. **IL2CPP build:** Verify `TaskCompletionSource<HttpHeaders>` and `TaskCompletionSource<bool>` are not stripped (22b.3, 22b.1).
4. **Allocation gate test:** Verify no new per-request allocations for non-100-continue, non-trailer requests. The serialization split (22b.1) and TCS-only-for-chunked optimization (22b.3) must not regress the allocation profile.

## Review Model

Both specialist agent reviews are mandatory per sub-phase:
- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

## Deferred Until Post-22b

1. **`Func<Task<HttpHeaders>>` async trailer provider** — sync `Func<HttpHeaders>` is sufficient for known use cases.
2. **Cache format trailer persistence** — trailers are lost on cache replay.
3. **`AutoExpectContinueThresholdBytes` for unknown-length bodies** — only known-length bodies trigger the automatic threshold.
4. **HTTP/2 CONNECT tunnel ALPN upgrade** — Phase 22c.
5. **gRPC-Web trailer interop testing** — Phase 25.
