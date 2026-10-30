# Project status

Remaining work is tracked as GitHub issues by milestone (v1.0-alpha → v1.0-beta → v1.0 GA → v1.1 →
v1.2 → v2.0) and in three projects (v1.0 Release, Protocol & Security, Post-1.0 Roadmap). This file
only maps phases to their plans and journal entries; issue state on GitHub wins over it.

Plans: `Development/docs/phases/`. Journal: `Development/docs/implementation-journal/` (find a
phase's entries with `ls Development/docs/implementation-journal | grep phase<N>`).

| Phase | Status | Open work |
| --- | --- | --- |
| 1 Foundation, 2 Core types | done | — |
| 3 Client API + HTTP/1.1 (3.1–3.5) | done | detailed plans in `phases/phase3/`; `phase-03-client-api.md` is historical only. Device gate for transport registration never run (#1) |
| 3B HTTP/2 | done | protocol hardening in v1.0-beta (#11–#32) |
| 3C BouncyCastle TLS fallback | done, unsafe | chain validation missing (#33, #34) |
| 4 Pipeline, 5 Content handlers | done | later rewritten as interceptors (22.x); docs still use old names (#107, #108) |
| 6 Performance & hardening, 7 Testing infra | done | soak test (#114) |
| 8 Docs & samples | not started | #98 |
| 9 Platform compatibility | partial (detection/config only) | device matrix #99 |
| 10 Advanced middleware (cache, redirect, cookies) | done | module findings in v1.0-beta/v1.1 |
| 11 Unity integration | done | — |
| 12 Editor tools (HTTP Monitor) | done | capture redaction/memory #92 |
| 14 Post-v1.0 features (Happy Eyeballs, proxy, background, adaptive, OAuth, interceptors, mock server, plugins) | done | background networking experimental #106 |
| 15 Unity runtime hardening | implemented | certification matrix #105 |
| 16 Rate limiting, WebGL, security/privacy, GraphQL, parallel helpers | planned | #118, #130, #117, #131, #119 |
| 17 CI/CD & release | CI done (lint, CodeQL, Unity tests pending secrets) | #100, #101 |
| 18 WebSocket client, 18a advanced WebSocket | done / implemented, verification pending | #104 and WebSocket issues |
| 19, 19a (zero-alloc), 19b (unified pipeline) | done | 19c planned (#120) |
| 22 Interceptor rewrite (22.1–22.4) | done | — |
| 22a End-to-end streaming | 22a.1–22a.5 done | 22a.6 validation #102 |
| 22b Expect 100-continue, proxy streaming, trailers | done | plan/semantics fixes #109 |
| 22c HTTP/2 through CONNECT | Editor-validated, device validation pending | #103 |
| 22d HTTP/2 proxy leg + proxy-aware H2 pooling | planned (clean-break proxy refactor allowed) | plan fixes #125, then #126–#129 (v2.0) |
| 23 API config, 24 content handlers, 25 gRPC | planned | #121, #122–#124, #132 |

Phase 13 has no plan document.

Memory targets: < 500 B GC per request on the buffered fast path (Phase 6 goal), enforced by the
allocation gates in `Tests/Runtime/Performance/`.
