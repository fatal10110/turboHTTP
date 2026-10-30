---
key: PH-08
title: [Phase 8] Rewrite README, Documentation~ and Samples~ against the interceptor API
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, type:docs, area:docs, priority:P0, source:roadmap
---
## Summary
Phase 8 (Documentation & Samples) is not started, and every customer-facing doc uses an API that no longer exists. README Quick Start, `Documentation~/*` and all five `Samples~` use `options.Middlewares`, `RetryMiddleware`/`AuthMiddleware`/`CacheMiddleware`/`RateLimitMiddleware`/`MetricsMiddleware` and `.SendAsync()`. The real API is `UHttpClientOptions.Interceptors`, `*Interceptor` classes, and `SendBufferedAsync` / `SendStreamingAsync`. Samples will not compile when imported. README also claims AssetBundle support that does not exist (Phase 24.2).

## Scope (from `Development/docs/phases/phase-08-documentation.md`, updated)
- [ ] 8.1 Quick Start (GET, POST JSON, typed JSON, headers, timeout, error handling) on the real API
- [ ] 8.2 API reference: `UHttpClient`, `UHttpRequest`, `UHttpResponse`, `UHttpClientOptions`, interceptors, JSON/Unity extensions, errors
- [ ] Interceptor ordering guide (security-relevant: Redirect/Auth/Cache/Cookie/Retry/Concurrency order) — pairs with [[MOD-06]]
- [ ] Streaming vs buffered responses and body ownership/disposal
- [ ] Module asmdef/auto-reference guidance ([[REL-07]])
- [ ] TLS backends (SslStream / BouncyCastle / Auto) and their security properties after [[TLS-01]] / [[TLS-02]]
- [ ] Platform notes: iOS ATS, Android cleartext, IPv6/NAT64, WebGL unsupported ([[REL-09]])
- [ ] Proxy configuration, HTTP/2 through CONNECT (default-off status, [[PH-22c]])
- [ ] WebSocket usage (events vs `ReceiveAsync`)
- [ ] 5 samples rewritten, each with its own asmdef, compiled in CI
- [ ] Remove the AssetBundle claim (or ship [[PH-24.2]])
- [ ] `Documentation~/Modules/Middleware.md` renamed/rewritten (no `Middlewares` property)

## Acceptance criteria
- [ ] Every code snippet in README/Documentation~ compiles (doc-snippet test project or samples).
- [ ] Each sample imports into a clean 2021.3 project and runs.
- [ ] No references to removed types (`grep -r "Middleware\b\|SendAsync(" Documentation~ Samples~ README.md` clean, except deliberate history notes).

## Context
Source: roadmap Phase 8 + 2026-10 full review (blocker B6).

===ISSUE===
---
key: PH-09
title: [Phase 9] Physical-device IL2CPP validation matrix (iOS + Android)
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, type:test, area:mobile, area:transport, priority:P0, platform:il2cpp, needs-device-validation, source:roadmap
---
## Summary
No device or IL2CPP run is recorded anywhere. Phase 9's journal covers only `PlatformInfo`/`PlatformConfig`. The overview's gate ("validate on a physical iOS device before proceeding past 3B") was never met. Phases 22a.6, 22b, 22c and 22d all list device validation as pending.

## Matrix
Targets: iOS (oldest supported + current, ARM64), Android (ARMv7 32-bit + ARM64, API min + current), Standalone Mono + IL2CPP. Managed stripping: Low and High.

Scenarios:
- [ ] Transport + WebSocket transport auto-registration ([[REL-01]])
- [ ] link.xml / linker processor preservation ([[REL-08]])
- [ ] SslStream ALPN availability; Auto backend selection does not fall back to unvalidated BouncyCastle ([[TLS-02]])
- [ ] BouncyCastle HTTP/2 (ALPN h2) incl. through CONNECT ([[PH-22c]]) and concurrent read/write ([[TLS-04]])
- [ ] HTTP/1.1 and HTTP/2 against real servers (nginx, Cloudflare, Go)
- [ ] Expect: 100-continue concurrent read/write on both TLS providers
- [ ] iOS ATS / Android cleartext and network security config behavior
- [ ] IPv6-only network (NAT64, Apple review requirement)
- [ ] Network switch Wi-Fi ↔ LTE, airplane mode, app suspend/resume (HTTP/2 PING liveness, pooled connection reuse)
- [ ] WebSocket (ws/wss, permessage-deflate) incl. background/foreground
- [ ] SAEA and PollSelect socket modes
- [ ] Texture/Audio handlers, MainThreadDispatcher
- [ ] Allocation/latency benchmarks on device (22a.6)

## Acceptance criteria
- [ ] Results recorded in `Development/docs/implementation-journal/` with device, OS, Unity version, scripting backend, stripping level.
- [ ] All P0/P1 failures filed as issues and linked here.
- [ ] Matrix automated where possible in [[PH-17a]] (device farm or Unity Build Automation).

## Context
Source: roadmap Phase 9 (`Development/docs/phases/phase-09-platform-compat.md`, `phase9/`) + 2026-10 full review (B8).

===ISSUE===
---
key: PH-17a
title: [Phase 17.1] CI pipeline: EditMode/PlayMode tests, IL2CPP player builds, .meta check
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, area:ci, priority:P1, source:roadmap
---
## Summary
There is no `.github/workflows` and no CI of any kind. Phase 17.1 plans GitHub Actions CI.

## Scope
- [ ] game-ci (or Unity Build Automation) workflow on 2021.3 LTS + latest LTS
- [ ] EditMode + PlayMode test runs (`TurboHTTP.Tests.Runtime`, `TurboHTTP.Tests.Editor`), excluding `ExternalNetwork` category ([[TEST-05]])
- [ ] IL2CPP player builds for iOS (Xcode project) and Android (APK) with stripping High — build must succeed (catches [[REL-01]], [[REL-08]], Android plugin Gradle break)
- [ ] `.meta` completeness check ([[REL-02]])
- [ ] Samples compile check ([[PH-08]])
- [ ] Transport fuzz job on plain .NET ([[TEST-02]])
- [ ] Test result + coverage artifacts
- [ ] Unity license secrets configured

## Acceptance criteria
- [ ] Every PR runs the workflow; main is protected on it.
- [ ] Workflow green on main.

## Context
Source: roadmap Phase 17.1 (`Development/docs/phases/phase-17-release.md`).

===ISSUE===
---
key: PH-17b
title: [Phase 17.2-17.6] Asset Store packaging, versioning strategy and submission
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, area:packaging, priority:P1, source:roadmap
---
## Summary
Phase 17 tasks 17.2–17.6 (build script, Asset Store package builder, release checklist, versioning, store metadata) are not started.

## Scope
- [ ] 17.2 Build script producing the release artifact (respecting [[REL-06]] exclusions)
- [ ] 17.3 Asset Store package builder / upload via Asset Store Publisher tools
- [ ] 17.4 Release checklist (code quality, tests, docs, package, build, submission, communication, monitoring) — add: third-party notices ([[REL-05]]), license ([[REL-04]]), device matrix sign-off ([[PH-09]])
- [ ] 17.5 Versioning strategy (SemVer, preview tags, CHANGELOG discipline)
- [ ] 17.6 Asset Store metadata: description, supported platforms (no WebGL), Unity version, screenshots, third-party disclosure

## Acceptance criteria
- [ ] Reproducible release artifact from CI.
- [ ] Submission checklist fully ticked.

## Context
Source: roadmap Phase 17.

===ISSUE===
---
key: PH-22a6
title: [Phase 22a.6] Complete streaming validation, benchmarks and IL2CPP confirmation
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, type:test, area:core, area:transport, priority:P1, platform:il2cpp, needs-device-validation, source:roadmap
---
## Summary
Phase 22a (end-to-end request/response streaming) is implemented across Core, Transport and modules; 22a.6 (validation) is the active work. `StreamingAllocationGateTests.cs` landed; the functional test list, benchmarks and mobile/IL2CPP confirmation are open.

## Scope (from `Development/docs/phases/phase22a/phase-22a.6-validation.md`)
- [ ] Step 1 functional coverage: core body model, HTTP/1.1 and HTTP/2 transport streaming tests, interceptor streaming tests
- [ ] Allocation gates (HTTP/1.1, HTTP/2, decompression) — done; extend to retry/redirect/cache replay paths
- [ ] Async state machine boxing validation under IL2CPP (hot-path `ReadAsync` completes synchronously when buffered)
- [ ] Latency/throughput benchmarks vs buffered baseline
- [ ] Device confirmation via [[PH-09]]
- [ ] Note: Cache re-buffering defeats streaming ([[MOD-03]]) — validation should cover it after the fix
- [ ] Update CLAUDE.md/AGENTS.md status

## Acceptance criteria
- [ ] All 22a.6 checklist items ticked with evidence in the journal.
- [ ] Phase 22a marked COMPLETE.

## Context
Source: roadmap Phase 22a.6.

===ISSUE===
---
key: PH-22c
title: [Phase 22c] Device-validate HTTP/2 through CONNECT (BouncyCastle ALPN h2); ship default-off until validated
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, area:http2, area:proxy, area:tls, priority:P1, platform:il2cpp, needs-device-validation, source:roadmap
---
## Summary
Phase 22c (HTTP/2 through CONNECT proxy tunnels) passed Editor PlayMode validation; physical iOS/Android IL2CPP validation of BouncyCastle ALPN `h2` through CONNECT remains required before COMPLETE. Several 22c.4 checklist items are still unticked (GOAWAY through tunnel, retained-connection reuse, manager key checks).

## Scope
- [ ] Finish unticked items in `Development/docs/phases/phase22c/phase-22c.4-validation.md`
- [ ] Device validation on iOS + Android IL2CPP ([[PH-09]]) — blocked on BouncyCastle chain validation ([[TLS-01]]) and duplex stream ([[TLS-04]])
- [ ] Until validated: option to disable h2 inside CONNECT tunnels, default off (HTTP/1.1 in tunnel)
- [ ] Re-run after HTTP/2 stream-lifecycle fixes (v1.0-beta HTTP/2 issues)

## Acceptance criteria
- [ ] Device results recorded; CLAUDE.md status → COMPLETE, or feature remains default-off with documented rationale.

## Context
Source: roadmap Phase 22c + 2026-10 full review.

===ISSUE===
---
key: PH-18a
title: [Phase 18a] Close WebSocket advanced-feature verification gates or ship as experimental
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, area:websocket, priority:P2, source:roadmap
---
## Summary
Phase 18a (labelled v1.2) is "mostly implemented in code, not fully closed": runner-verified test passes and the three pre-implementation spike validations (incl. `DeflateStream.Flush()` behavior gating permessage-deflate) are not recorded. The 2026-10 review found permessage-deflate parameter handling bugs.

## Scope
- [ ] Run and record the full WebSocket runtime test suites
- [ ] Record spike results (DeflateStream flush semantics on Mono/IL2CPP, etc.)
- [ ] Fix or disable permessage-deflate per the WebSocket v1.0-beta issues
- [ ] Decide: GA feature vs `[Experimental]`/opt-in for v1.0

## Acceptance criteria
- [ ] Journal entry with verification evidence; status updated in CLAUDE.md/AGENTS.md.

## Context
Source: `Development/docs/implementation-journal/2026-02-phase18a-implementation-status.md`.

===ISSUE===
---
key: PH-15
title: [Phase 15.6] Run the Unity reliability certification matrix
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, type:test, area:unity, priority:P2, needs-device-validation, source:roadmap
---
## Summary
Phase 15 overview: "Phase 15 is incomplete until certification matrix runs green across supported targets." 15.6 defines reliability/stress suites (dispatcher flood, texture bursts with frame budgets, audio temp-file churn, cancellation/reload transitions) and performance budgets.

## Scope
- [ ] `UnityReliabilitySuiteTests` / `UnityStressSuiteTests` present and green
- [ ] Performance budgets enforced with machine-readable metrics
- [ ] Matrix run on supported targets (with [[PH-09]])
- [ ] Cover leak fixes from the Unity v1.0-beta issues (Texture2D/AudioClip on cancel)

## Acceptance criteria
- [ ] Certification matrix green; results in the journal.

## Context
Source: `Development/docs/phases/phase15/phase-15.6-unity-reliability-test-gate.md`.

===ISSUE===
---
key: PH-14.3
title: [Phase 14.3] Gate background networking (iOS/Android native bridges) as experimental for v1.0
milestone: v1.0 GA: Docs, validation & release
labels: type:phase, area:mobile, priority:P1, needs-device-validation, source:roadmap
---
## Summary
Phase 14.3 shipped iOS `beginBackgroundTask` and Android WorkManager bridges that have never been built or verified on a device. The review found a foreground-request cancellation bug (iOS `DBL_MAX` overflow), MainThreadDispatcher dependency while backgrounded, and an Android Gradle break from the Kotlin plugin dependency.

## Scope
- [ ] Off by default; mark API `[Experimental]` / documented opt-in
- [ ] Android plugin packaged so it does not break builds when unused (see Mobile release-blocker issue)
- [ ] Device validation of begin/end, expiration, deferred work on iOS + Android ([[PH-09]])
- [ ] Fix-forward the background-networking bugs in v1.1

## Acceptance criteria
- [ ] A project that never enables background networking builds and runs on Android and iOS with no behavior change.
- [ ] Docs describe experimental status.

## Context
Source: roadmap Phase 14.3 + 2026-10 full review.

===ISSUE===
---
key: DOC-01
title: [Docs] CLAUDE.md, AGENTS.md and architecture.md describe removed API and stale status
milestone: v1.0 GA: Docs, validation & release
labels: type:docs, area:docs, priority:P1, source:review-2026-10
---
## Summary
Agent/architecture docs drive all future work but are out of date:
- Describe `HttpPipeline`, `IHttpMiddleware`, `UHttpRequestBuilder`, `SendAsync`, Logging/Retry/Auth/Metrics/DefaultHeaders/Concurrency *Middleware* — code is `InterceptorPipeline`, `IHttpInterceptor`/`IHttpHandler`, `*Interceptor`, `SendBufferedAsync`/`SendStreamingAsync`.
- Call `UHttpRequest` immutable; it is mutable and pooled.
- "13 runtime + 1 editor" assemblies and a `TurboHTTP.Performance` module — actual: 18 runtime asmdefs, no Performance module; WebSocket, WebSocket.Transport, Unity.WebSocket, UniTask, Transport.BouncyCastle missing from the list.
- "Optional modules never reference each other" — false (Unity.WebSocket→Unity+WebSocket, UniTask→WebSocket, WebSocket.Transport→Transport, BouncyCastle→Transport, Editor→Observability). AGENTS.md has the correct exception list.
- Development Status: 22b.1/22b.2 listed pending (implemented); "Phase 8 + 12–14 not started" (12, 14 done); 9, 15, 18, 18a, 19 missing.

## Acceptance criteria
- [ ] CLAUDE.md and AGENTS.md aligned with code and with each other.
- [ ] `Development/docs/architecture.md` §3 updated to the interceptor model.
- [ ] Status section reflects the milestones from [[REL-10]].

## Context
Source: 2026-10 full review (docs drift).

===ISSUE===
---
key: DOC-02
title: [Docs] 00-overview.md roadmap and CHANGELOG are stale
milestone: v1.0 GA: Docs, validation & release
labels: type:docs, area:docs, priority:P2, source:review-2026-10
---
## Summary
`Development/docs/00-overview.md`:
- dead link `phases/phase-16-platform-protocol-security.md` (real: `phase-16-advanced-capabilities.md`)
- says JSON is System.Text.Json (LiteJson is default)
- lists a Performance module
- GC target "<1KB" vs CLAUDE.md "<500B"
- phase list stops at 17 (18–25 missing)
- "Review Notes TODO" items (proxy, cookies, decompression) long done

`CHANGELOG.md`: "Unreleased" lists Phase 14 features; 1.0.0 entry lists non-existent modules.

## Acceptance criteria
- [ ] Overview lists all phases with the milestone mapping from [[REL-10]]; links valid.
- [ ] CHANGELOG matches shipped content (see [[REL-03]]).

## Context
Source: 2026-10 full review.

===ISSUE===
---
key: PLAN-01
title: [Plan] Fix Phase 22b.1 Expect: 100-continue semantics (early final response, 417, send-failure rule)
milestone: v1.0-beta: Protocol & correctness hardening
labels: type:plan, area:http1, area:http2, priority:P1, source:review-2026-10
---
## Summary
22b.1 is implemented, but the plan (and code) have protocol gaps:
1. §6f: after an early final response (e.g. 417 with keep-alive) the HTTP/1.1 connection returns to the pool although the declared body was never sent — request desync (code fix: [[H1-01]]).
2. RFC 9110 §10.1.1: a client SHOULD repeat the request without the expectation after 417; currently returned to caller.
3. Contradiction: Step 4 Stage 3.3 says surface the send failure even if a final response arrives; §6a says read the response after a write error. Code throws, so a 413 after partial upload surfaces as exception, not response.
4. Concurrent read/write during the 100-continue wait validated only on SslStream, not BouncyCastle `TlsClientProtocol` ([[TLS-04]]).
5. HTTP/2: early final response leaves the client side open (no END_STREAM/RST_STREAM); 1xx with END_STREAM not treated as malformed (RFC 9113 §8.1) — code fix in the HTTP/2 issues.

## Acceptance criteria
- [ ] Plan doc `phase22b/phase-22b.1-expect-100-continue.md` updated with one consistent rule for send-failure vs final response.
- [ ] 417 automatic retry for replayable bodies (or documented non-support).
- [ ] BouncyCastle added to the validation matrix.
- [ ] Test `RawSocketTransport_ExpectContinue_KeepAliveRejectionWithBody_ReusesConnectionAfterDrain` flipped.

## Context
Source: 2026-10 full review (plan quality). CLAUDE.md still lists 22b.1/22b.2 as pending although implemented ([[DOC-01]]).

===ISSUE===
---
key: PLAN-02
title: [Plan] Phase 22c overview principle 6 contradicts the implemented runtime TLS fallback
milestone: v1.0 GA: Docs, validation & release
labels: type:plan, area:tls, area:proxy, priority:P2, source:review-2026-10
---
## Summary
`phase22c/overview.md` principle 6 says provider selection is "pre-handshake only, no runtime catch-and-retry". The main 22c plan ("Decision: implement inline retry"), the 22c.4 Step 6 test and the code (`Runtime/Transport/Tcp/TcpConnectionPool.cs:594-620` → `MarkSslStreamBroken` + BouncyCastle retry) do the opposite. 22d.2 Step 4.5 repeats the "no retry" claim, so 22c and 22d tunnels would behave differently.

## Suggested fix
Decide the rule together with [[TLS-02]] (Auto must fail closed until BouncyCastle validates chains), then make 22c overview, 22c main plan, 22d.2 and code agree.

## Acceptance criteria
- [ ] Single documented fallback rule; docs and code consistent; test asserts it.

## Context
Source: 2026-10 full review (plan quality).

===ISSUE===
---
key: TEST-01
title: [Testing] HTTP/2 interop matrix and scripted adversarial server
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:http2, area:testing, priority:P1, source:review-2026-10
---
## Summary
All `Http2*Tests` run against in-house mocks. The 2026-10 review found HPACK desync, stream-ID ordering, flow-control wake-up and GOAWAY issues that mocks did not catch.

## Scope
- [ ] Interop runs against nghttpd/nghttpx, h2o, Go net/http, Kestrel, nginx, Envoy (Docker, plain .NET harness since Transport is `noEngineReferences`)
- [ ] Scripted adversarial server scenarios:
  - [ ] response HEADERS with incremental indexing on a stream the client cancelled (HPACK sync)
  - [ ] CONTINUATION flood / huge header block
  - [ ] HPACK bomb, dynamic table size changes mid-connection
  - [ ] GOAWAY with in-flight POSTs (graceful drain), REFUSED_STREAM
  - [ ] SETTINGS_INITIAL_WINDOW_SIZE decrease mid-upload (negative windows, RFC 9113 §6.9.2)
  - [ ] MAX_CONCURRENT_STREAMS lowered by peer; >100 concurrent requests with random cancellation
  - [ ] response with content-length mismatch
- [ ] Optional: h2spec-style client conformance checks

## Acceptance criteria
- [ ] Suite runs in CI ([[PH-17a]]); all scenarios green after the v1.0-beta HTTP/2 fixes.

## Context
Source: 2026-10 full review (test gaps).

===ISSUE===
---
key: TEST-02
title: [Testing] Fuzz protocol parsers (HTTP/1.1, chunked, HPACK, HTTP/2 frames, WebSocket frames, LiteJson)
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:testing, area:http1, area:http2, area:websocket, priority:P1, source:review-2026-10
---
## Summary
No fuzzing exists for any parser that consumes untrusted network input.

## Scope
Targets: `Http11ResponseParser` (status line, headers, obs-fold), chunked/trailer parser and `Http11ResponseBodySource`, `HpackDecoder` + Huffman, `Http2FrameCodec`/read loop, `WebSocketFrameReader` + `MessageAssembler` + permessage-deflate inflate, `JsonReader` (LiteJson).
- [ ] SharpFuzz (or libFuzzer via .NET) harness on plain .NET — Transport and JSON are `noEngineReferences`
- [ ] Seed corpora from existing tests
- [ ] Seeded randomized tests inside Unity Test Runner for regression
- [ ] CI job with a time budget ([[PH-17a]])

## Acceptance criteria
- [ ] Each target fuzzed ≥ 1M execs with no crash/hang/OOM; crashes filed as issues.

## Context
Source: 2026-10 full review (test gaps).

===ISSUE===
---
key: TEST-03
title: [Testing] Negative TLS test suite for SslStream and BouncyCastle providers
milestone: v1.0 GA: Docs, validation & release
labels: type:test, type:security, area:tls, area:testing, priority:P1, source:review-2026-10
---
## Summary
No test asserts that bad certificates are rejected. The BouncyCastle provider would currently accept an untrusted root ([[TLS-01]]).

## Scope
For each provider (SslStream, BouncyCastle, Auto):
- [ ] untrusted root / self-signed
- [ ] expired / not-yet-valid
- [ ] wrong hostname, wildcard abuse (`*.com`), CN-only cert when SAN present, IP SAN
- [ ] missing intermediate
- [ ] TLS < 1.2 server
- [ ] Auto mode never falls back to BouncyCastle on a cert failure ([[TLS-02]])
Use a local test CA (e.g. `badssl`-style docker or in-process server).

## Acceptance criteria
- [ ] All cases rejected with `UHttpErrorType` mapped to a certificate error, for every provider; runs in CI.

## Context
Source: 2026-10 full review (test gaps).

===ISSUE===
---
key: TEST-04
title: [Testing] Soak/leak test and lossy-network tests
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:testing, area:transport, priority:P2, source:review-2026-10
---
## Summary
The overview's criterion "1h soak, <10MB growth" is unmet. No test exercises HTTP/2 flow control or pooling under real latency/packet loss.

## Scope
- [ ] 1h soak mixing HTTP/1.1, HTTP/2, streaming, WebSocket, cancellations; assert managed memory, pool counters, socket count stable
- [ ] Netem/toxiproxy profiles: latency, loss, bandwidth limit, connection resets
- [ ] Mobile run of a shortened soak on device ([[PH-09]])

## Acceptance criteria
- [ ] Soak passes with < 10 MB growth and no leaked pool items; results journaled.

## Context
Source: 2026-10 full review (test gaps).

===ISSUE===
---
key: TEST-05
title: [Testing] Tag external-network tests so CI stays deterministic
milestone: v1.0 GA: Docs, validation & release
labels: type:test, area:testing, area:ci, priority:P3, source:review-2026-10
---
## Summary
Tests such as `PlatformTests.NetworkReachability_External_Google_RequestSucceeds` hit the public internet without the `ExternalNetwork` category, making CI flaky.

## Acceptance criteria
- [ ] All tests that need the internet carry `[Category("ExternalNetwork")]`.
- [ ] CI excludes the category by default; a nightly job runs it.

## Context
Source: 2026-10 full review (test gaps).
