# Project status

Remaining work is tracked as GitHub issues by milestone (v1.0-alpha → v1.0-beta → v1.0 GA → v1.1 →
v1.2 → v2.0) and in three projects (v1.0 Release, Protocol & Security, Post-1.0 Roadmap). This file
only maps phases to their plans and journal entries; issue state on GitHub wins over it.

Plans: `Development/docs/phases/`. Journal: `Development/docs/implementation-journal/` (find a
phase's entries with `ls Development/docs/implementation-journal | grep phase<N>`).

| Phase | Status | Open work |
| --- | --- | --- |
| 1 Foundation, 2 Core types | done | — |
| 3 Client API + HTTP/1.1 (3.1–3.5) | done | detailed plans in `phases/phase3/`; `phase-03-client-api.md` is historical only. Registration moved to the `TurboHttpBootstrap` (#216); player/device checks #100, #99. HTTP/1.1 parser and reuse hardening merged (#200, #215) |
| 3B HTTP/2 | done, hardened | v1.0-beta hardening merged in waves run w1030 (#198, #210, #224, #234, #241, #244); left: idle-stream/first-frame conformance #30 |
| 3C BouncyCastle TLS fallback | re-vendored (strip v2, #223); Auto fails closed (#195) | chain validation + RFC 6125 matching in the BC path (#240), native iOS/Android verifiers #239 (#33 stays open); full AES-256-GCM handshakes #230; Unity-only test failures #206 |
| 4 Pipeline, 5 Content handlers | done | later rewritten as interceptors (22.x); pre-22 phase docs still use old names (#108); old `docs/architecture.md` removed (#107, #189) |
| 6 Performance & hardening, 7 Testing infra | done | soak test (#114) |
| 8 Docs & samples | not started | #98 |
| 9 Platform compatibility | partial (detection/config only) | device matrix #99 |
| 10 Advanced middleware (cache, redirect, cookies) | done | module findings in v1.0-beta/v1.1 |
| 11 Unity integration | done | — |
| 12 Editor tools (HTTP Monitor) | done | capture redaction/memory #92 |
| 14 Post-v1.0 features (Happy Eyeballs, proxy, background, adaptive, OAuth, interceptors, mock server, plugins) | done | background networking experimental #106 |
| 15 Unity runtime hardening | implemented | certification matrix #105 |
| 16 Rate limiting, WebGL, security/privacy, GraphQL, parallel helpers | planned | #118, #130, #117, #131, #119 |
| 17 CI/CD & release | CI done (lint, CodeQL); PR CI does not run Unity tests (no license, #188) | full suites run in the periodic host-editor run (`testing.md`); #100, #101 |
| 18 WebSocket client, 18a advanced WebSocket | done / implemented, verification pending | beta fixes merged (#201, #218, #237); left: #82 (deflate params), reconnect race #208, #104 |
| 19, 19a (zero-alloc), 19b (unified pipeline) | done | 19c planned (#120) |
| 22 Interceptor rewrite (22.1–22.4) | done | — |
| 22a End-to-end streaming | 22a.1–22a.5 done | 22a.6 validation #102 |
| 22b Expect 100-continue, proxy streaming, trailers | done | plan/semantics fixes #109 |
| 22c HTTP/2 through CONNECT | Editor-validated, device validation pending | #103 |
| 22d HTTP/2 proxy leg + proxy-aware H2 pooling | planned (clean-break proxy refactor allowed) | plan fixes #125, then #126–#129 (v2.0) |
| 23 API config, 24 content handlers, 25 gRPC | planned | #121, #122–#124, #132 |

Phase 13 has no plan document.

Waves run w1030 (2026-10-30 to 2026-11-01) merged 29 PRs (#194–#244) for v1.0-alpha/beta: 77 of its
92 planned issues closed; partials left open: #1, #7, #8, #30, #33, #35, #82, #110, #142, #162, #167,
#168, #169, #181, #186. The first host-editor Unity run after it found #245 (hangs the PlayMode suite).

Memory targets: < 500 B GC per request on the buffered fast path (Phase 6 goal), enforced by the
allocation gates in `Tests/Runtime/Performance/`.
