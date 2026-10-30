---
key: REL-01
title: [Packaging] Default HTTP and WebSocket transport registration relies on unsupported [ModuleInitializer]
milestone: v1.0-alpha: Release blockers
labels: type:bug, area:packaging, area:transport, area:websocket, priority:P0, platform:il2cpp, needs-device-validation, confidence:plausible, source:review-2026-10
---
## Summary
`RawSocketTransport` and the raw-socket WebSocket transport register themselves only through a polyfilled C# 9 `[ModuleInitializer]`. Unity 2021.3 lists module initializers as unsupported, nothing references `TurboHTTP.Transport` / `TurboHTTP.WebSocket.Transport` statically, and there is no `RuntimeInitializeOnLoadMethod` backstop. On device, `new UHttpClient()` is likely to throw `No default transport configured` and `WebSocketTransportFactory.Create` to throw `No WebSocket transport factory configured`.

## Location
- `Runtime/Transport/TransportModuleInitializer.cs:18-22` — only registration path
- `Runtime/WebSocket.Transport/WebSocketTransportModuleInitializer.cs:7-10` — same pattern
- `Runtime/Core/HttpTransportFactory.cs:89` — throws when nothing registered
- `Runtime/Transport/link.xml` — does not preserve `RawSocketTransport` or the initializer
- `Runtime/Unity.WebSocket` cannot call `EnsureRegistered()` (no reference to `WebSocket.Transport`)

## Problem
- Mono players may never load the Transport assembly (no static reference), so the module initializer never runs.
- IL2CPP may ignore or strip `<Module>..cctor`.
- Tests mask the issue by calling `RawSocketTransport.EnsureRegistered()` explicitly. The Phase 3.5 "MANDATORY IL2CPP device gate" has no recorded result.

## Suggested fix
1. Add a small engine-referencing bootstrap (e.g. in `TurboHTTP.Complete` with a script, or a new `TurboHTTP.Bootstrap` asmdef referencing Core + Transport + WebSocket.Transport) with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` + `[Preserve]` that calls both `EnsureRegistered()` methods.
2. Preserve `RawSocketTransport`, the WebSocket transport factory and both initializers in link.xml (see [[REL-08]] for link.xml being honored at all).
3. Keep the module initializer as a secondary path; document `EnsureRegistered()` as the explicit fallback.

## Acceptance criteria
- [ ] IL2CPP iOS and Android player builds (Managed Stripping Level: High) create `UHttpClient` and a WebSocket client without calling `EnsureRegistered()` manually.
- [ ] Mono standalone player does the same.
- [ ] Player-build smoke test added to CI ([[PH-17a]]).

## Context
Source: 2026-10 full review (`Development/docs/review-2026-10-full-library-review.md`, blocker B3). Reported independently by three reviewers. Confidence: Plausible (needs device build).

===ISSUE===
---
key: REL-02
title: [Packaging] Commit missing .meta files and add a CI guard
milestone: v1.0-alpha: Release blockers
labels: type:chore, area:packaging, area:ci, priority:P0, source:review-2026-10
---
## Summary
Ten runtime `.cs` files (including a public Core type), ~10 test files, ~50 docs and the `phase22b/`, `phase22c/`, `phase22d/` folders have no `.meta`. When the package is installed as an immutable UPM package (git URL, registry, tarball), Unity ignores files without `.meta`, so the package fails to compile.

## Location
Runtime files missing `.meta`:
- `Runtime/Core/StreamingOptions.cs` (public)
- `Runtime/Core/DetachedBufferedBody.cs`
- `Runtime/Core/Internal/ResponseBodyDiscardHelper.cs`
- `Runtime/Cache/AssemblyInfo.cs`
- `Runtime/Cache/StreamingCacheAccumulator.cs`
- `Runtime/Observability/ObservedResponseBodySource.cs`
- `Runtime/Transport/Http2/Http2MonotonicClock.cs`
- `Runtime/Transport/Http2/Http2ResponseBodySource.cs`
- `Runtime/Transport/Internal/ExpectContinueHelper.cs`
- `Runtime/Transport/Internal/TrailerFieldValidator.cs`

## Suggested fix
1. Open the package in a Unity project once to generate metas, commit them (stable GUIDs).
2. Add a CI step that fails if any tracked file/folder under `Runtime/`, `Editor/`, `Tests/`, `Plugins/`, `Documentation~`-excluded paths lacks a sibling `.meta`:
   ```bash
   git ls-files Runtime Editor Tests Plugins | grep -v '\.meta$' | while read f; do [ -f "$f.meta" ] || echo "missing $f.meta"; done
   ```

## Acceptance criteria
- [ ] Zero missing `.meta` files for tracked assets.
- [ ] CI check fails on a deliberately added file without `.meta`.
- [ ] Package installs via git URL into a clean 2021.3 project and compiles.

## Context
Source: 2026-10 full review (blocker B4). Confidence: Confirmed.

===ISSUE===
---
key: REL-03
title: [Packaging] Replace placeholder package.json metadata and align version with CHANGELOG
milestone: v1.0-alpha: Release blockers
labels: type:chore, area:packaging, priority:P0, source:review-2026-10
---
## Summary
`package.json` still ships template metadata and claims `1.0.0` while `CHANGELOG.md` says `[1.0.0] - TBD`.

## Location
- `package.json` — `author.name: "Your Name/Company"`, `author.email: "support@yourcompany.com"`, `author.url: "https://yourcompany.com"`, `version: "1.0.0"`, `license: "Custom/Dual-License"`
- `CHANGELOG.md` — 1.0.0 entry lists a non-existent "Performance module" and "Middleware" (replaced by interceptors)

## Suggested fix
- Real publisher name, support email and URL.
- Pre-release version (e.g. `1.0.0-preview.1`) until GA; bump on release.
- `license` field consistent with the decision in [[REL-04]].
- Rewrite CHANGELOG entries to match what actually ships.

## Acceptance criteria
- [ ] No placeholder strings in `package.json`.
- [ ] `package.json` version and top CHANGELOG entry agree.
- [ ] CHANGELOG lists only modules that exist.

## Context
Source: 2026-10 full review (blocker B7). Confidence: Confirmed.

===ISSUE===
---
key: REL-04
title: [Legal] LICENSE.md grants MIT-style rights for a commercial closed-source product
milestone: v1.0-alpha: Release blockers
labels: type:chore, area:packaging, priority:P0, source:review-2026-10
---
## Summary
`LICENSE.md` is an MIT-style grant ("copy, modify, merge, publish, distribute, sublicense, and/or sell") for personal/small-company use, which conflicts with the stated closed-source per-seat commercial model. Asset Store sales are governed by the standard Asset Store EULA. `CONTRIBUTING.md` invites outside contributions. Copyright line reads "Copyright (c) 2024 TurboHTTP Authors". The GitHub repository is public, so the full source is currently visible.

## Problem
- Customers (and anyone reading the public repo) may claim redistribution rights.
- Outside contributions without a CLA complicate commercial licensing.

## Suggested fix
1. Legal review of the licensing model (Asset Store EULA only vs dual license).
2. Replace `LICENSE.md` accordingly; align `package.json` `license`.
3. Remove or rewrite `CONTRIBUTING.md` (or add a CLA).
4. Decide repository visibility consistent with the license.

## Acceptance criteria
- [ ] License text reviewed and replaced.
- [ ] `package.json` license field matches.
- [ ] Contribution policy documented.

## Context
Source: 2026-10 full review (blocker B7). Confidence: Confirmed.

===ISSUE===
---
key: REL-05
title: [Legal] Add third-party notices for vendored BouncyCastle and record upstream version
milestone: v1.0-alpha: Release blockers
labels: type:chore, area:packaging, area:tls, priority:P0, source:review-2026-10
---
## Summary
~1,268 BouncyCastle C# files are vendored under `Runtime/Transport/BouncyCastle/Lib` (re-namespaced to `TurboHTTP.SecureProtocol`) with no LICENSE/NOTICE and no record of the upstream version/commit. BouncyCastle C# is MIT-licensed and requires the notice to be retained. The Asset Store also requires third-party component disclosure. Bundled decoders (NLayer, NVorbis, StbImageSharp) need the same check.

## Location
- `Runtime/Transport/BouncyCastle/Lib/**`
- `Editor/RepackageBouncyCastle.cs` (repackaging tool)
- `Runtime/Unity/Decoders/*` (third-party decoder ports)

## Suggested fix
- Add `Third-Party Notices.md` at package root with BouncyCastle MIT text and every other bundled third-party component.
- Record upstream repo URL + tag/commit + local modifications in `Runtime/Transport/BouncyCastle/README.md`.
- Add a step to the release checklist to refresh notices.

## Acceptance criteria
- [ ] Notices file present and shipped in the package.
- [ ] Upstream version recorded.
- [ ] Asset Store submission form third-party section filled.

## Context
Source: 2026-10 full review (blocker B7). Confidence: Confirmed.

===ISSUE===
---
key: REL-06
title: [Packaging] Stop shipping internal material and decide UPM vs .unitypackage distribution
milestone: v1.0-alpha: Release blockers
labels: type:chore, area:packaging, priority:P1, source:review-2026-10
---
## Summary
`Development/` has a `.meta`, so Unity imports it into customer projects, including internal security reviews and plans. `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `.claude/`, and the `Editor/RepackageBouncyCastle.cs` menu tool would also ship. `Samples~`, `Documentation~` and `package.json` samples only work for UPM distribution, not for a classic `.unitypackage` import into `Assets/`.

## Suggested fix
1. Rename `Development/` to `Development~` (Unity ignores `~` folders) or move it outside the package root.
2. Exclude agent docs and internal editor tools from the exported package (export script allow-list).
3. Decide the distribution format (Asset Store UPM package vs `.unitypackage`), and adapt samples/docs and link.xml handling ([[REL-08]]) accordingly.

## Acceptance criteria
- [ ] Exported package contains only runtime, editor (customer-facing), tests (optional), samples, docs, notices.
- [ ] Distribution format documented in the release checklist ([[PH-17b]]).

## Context
Source: 2026-10 full review (packaging). Confidence: Confirmed.

===ISSUE===
---
key: REL-07
title: [Packaging] Optional modules are invisible to Assembly-CSharp users
milestone: v1.0-alpha: Release blockers
labels: type:architecture, area:packaging, area:core, priority:P1, source:review-2026-10
---
## Summary
Every module except Core has `autoReferenced: false`, and `TurboHTTP.Complete` contains no scripts (the `Runtime/` root holds only the asmdef), so it compiles to nothing and re-exports nothing. Users writing code in `Assembly-CSharp` (no own asmdef — the common case) cannot use `AsJson`, Retry, Auth, Cache, or call `RawSocketTransport.EnsureRegistered()`, contrary to README examples.

## Location
- `Runtime/TurboHTTP.Complete.asmdef` — references all modules, `autoReferenced: true`, no `.cs` files
- `Runtime/*/TurboHTTP.*.asmdef` — `autoReferenced` unset (false) for every optional module

## Suggested fix
Pick one and document it:
- Make user-facing modules (`JSON`, `Retry`, `Auth`, `Cache`, `Middleware`, `Observability`, `Files`, `Unity`, `Transport`, `WebSocket*`) `autoReferenced: true`; or
- Ship clear asmdef guidance plus a sample asmdef in every sample folder.
Also give `Complete` a real purpose (e.g. the bootstrap from [[REL-01]]) or remove it.

## Acceptance criteria
- [ ] A script in `Assets/` with no asmdef compiles against the README Quick Start (JSON + Retry + Auth).
- [ ] Samples compile when imported ([[PH-08]]).

## Context
Source: 2026-10 full review (packaging). Confidence: Confirmed.

===ISSUE===
---
key: REL-08
title: [Packaging] link.xml files inside the UPM package are not honored by UnityLinker
milestone: v1.0-alpha: Release blockers
labels: type:bug, area:packaging, priority:P1, platform:il2cpp, needs-device-validation, confidence:plausible, source:review-2026-10
---
## Summary
UnityLinker does not scan `link.xml` files inside UPM packages; there is no `IUnityLinkerProcessor` in `Editor/`. Reflection-dependent paths (Auth→JSON via `ProjectJsonBridge`, `GetJsonCoroutine<T>`, Monitor auto-wiring, transport registration) rely on those link.xml files. Separately, `Runtime/Transport/link.xml` preserves the whole BouncyCastle assembly, so ~1,268 files ship in every IL2CPP build even though BouncyCastle is described as optional.

## Location
- `Runtime/*/link.xml`
- `Runtime/Transport/link.xml` — BouncyCastle preserve rule; SslStream rules under `System.Net.Security` (see TLS issue about wrong assembly)

## Suggested fix
1. Add `Editor/TurboHttpLinkerProcessor.cs : IUnityLinkerProcessor` whose `GenerateAdditionalLinkXmlFile` returns a merged link.xml from the package.
2. Move the BouncyCastle preserve rule into the BouncyCastle folder/assembly so it applies only when that assembly is included.
3. Replace reflection where possible with registration delegates (see [[MOD-18]] for the Auth→JSON bridge).

## Acceptance criteria
- [ ] IL2CPP build with Managed Stripping Level: High keeps all reflection targets (smoke test covers OAuth refresh parse, `GetJsonCoroutine`, transport registration).
- [ ] An IL2CPP build without BouncyCastle in use does not contain `TurboHTTP.SecureProtocol` types.

## Context
Source: 2026-10 full review (packaging/platform). Reference: Unity `IUnityLinkerProcessor.GenerateAdditionalLinkXmlFile`. Confidence: Plausible.

===ISSUE===
---
key: REL-09
title: [Packaging] WebGL builds compile but fail at runtime
milestone: v1.0-alpha: Release blockers
labels: type:bug, area:packaging, priority:P1, source:review-2026-10
---
## Summary
WebGL is deferred, but only `Transport`, `Complete`, `Transport.BouncyCastle` and `WebSocket.Transport` exclude WebGL. Core (auto-referenced) and JSON, Retry, Cache, Auth, Middleware, WebSocket, Unity.WebSocket, Unity and UniTask compile for WebGL, so a WebGL build succeeds and throws at runtime (`No default transport configured`). The runtime test asmdef references Transport without excluding WebGL.

## Suggested fix
- Either exclude WebGL in all runtime asmdefs, or add an `IPreprocessBuildWithReport` that fails WebGL builds with a clear "TurboHTTP does not support WebGL in v1.0" message.
- State "WebGL unsupported" in `PlatformNotes`, README and the Asset Store page.

## Acceptance criteria
- [ ] WebGL build fails at build time with an actionable message (or compiles nothing from TurboHTTP).
- [ ] Docs state platform support explicitly.

## Context
Source: 2026-10 full review. WebGL support itself is tracked in [[PH-16.2]]. Confidence: Confirmed.

===ISSUE===
---
key: REL-10
title: [Plan] Feature freeze and re-baseline the v1.0 roadmap
milestone: v1.0-alpha: Release blockers
labels: type:plan, area:docs, priority:P0, source:review-2026-10
---
## Summary
Phases 18/18a (labelled v1.2), 19, and 22a–22d ("M4 v2.0 follow-up") were built before the release-gating phases (8 docs, 9 device validation, 17 CI/release). The overview's own rule — "Do not proceed past Phase 3B without validating this on a physical iOS device" — was never met. Phase 14 ("Post-v1.0 Roadmap") was executed before release.

## Decision proposed
- Freeze new features until v1.0 GA.
- v1.0 scope = current code + fixes in milestones v1.0-alpha / v1.0-beta / v1.0 GA.
- Defer to v1.x/v2.0: 22d ([[PLAN-03]]), 25 gRPC, 19c, 23, 24 (except decompression, already done), 16.1/16.2/16.4/16.5, 14.3 background networking as experimental ([[PH-14.3]]), 18a as experimental unless verified ([[PH-18a]]).
- Ship 22c h2-through-CONNECT default-off until device-validated ([[PH-22c]]).

## Minimal path to v1.0
1. Release blockers (this milestone).
2. Protocol & correctness hardening (v1.0-beta).
3. Docs/samples rewrite ([[PH-08]]).
4. iOS/Android IL2CPP matrix ([[PH-09]]).
5. CI ([[PH-17a]]) and release ([[PH-17b]]).

## Acceptance criteria
- [ ] `Development/docs/00-overview.md` and CLAUDE.md/AGENTS.md updated with the new milestone mapping ([[DOC-01]], [[DOC-02]]).
- [ ] Each deferred phase doc carries its new target milestone.

## Context
Source: 2026-10 full review (roadmap). Confidence: Confirmed.
