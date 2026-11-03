# 2026-10 Transport bootstrap, module visibility, WebGL build guard (#1, #7, #9)

Lane `bootstrap` of waves run w1030 (v1.0-alpha release blockers).

## What

- **#1 transport registration.** New `Runtime/Bootstrap/TurboHttpBootstrap.cs` in the
  `TurboHTTP.Complete` assembly (the asmdef sits at `Runtime/`, so the folder without its own asmdef
  belongs to it). `RegisterDefaultTransports()` carries
  `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` + `[Preserve]` (type and method) and, in
  the Editor, `[InitializeOnLoadMethod]`; it calls `RawSocketTransport.EnsureRegistered()` and
  `RawSocketWebSocketTransport.EnsureRegistered()`. The `[ModuleInitializer]` classes stay as a
  secondary path; their docs, the factories' error messages, the `EnsureRegistered` docs and the
  link.xml comments now name the bootstrap. `Runtime/Bootstrap/AssemblyInfo.cs` exposes internals to
  `TurboHTTP.Tests.Runtime`.
- **#7 module visibility.** Option (a): `autoReferenced: true` on Transport, JSON, Retry, Auth,
  Cache, Middleware, RateLimit, Observability, Files, Unity, WebSocket, WebSocket.Transport and
  Unity.WebSocket. Testing, UniTask (define-constrained) and Transport.BouncyCastle (opt-in TLS
  backend) stay `false`. README and QuickStart explain the asmdef rule.
- **#9 WebGL.** New `Editor/TurboHttpWebGlBuildGuard.cs` (`IPreprocessBuildWithReport`,
  `callbackOrder -1000`) throws `BuildFailedException` for `BuildTarget.WebGL` with an actionable
  message. PlatformNotes, README (new "Platform Support" section), QuickStart and the Asset Store
  description draft (phase-17 task 17.6; the store text is not in the repo yet) say "unsupported in
  v1.0". `TurboHTTP.Tests.Runtime` now excludes WebGL (it references Transport, which already did).

## Decisions

- **#7 option (a), not (b).** Assembly-CSharp references only `autoReferenced` assemblies and asmdef
  references are not transitive, so the phase-01 note (`phase-01-project-foundation.md:660-662`,
  "Complete provides an autoReferenced all-in entry point") cannot work: an autoReferenced
  Complete does not make `TurboHTTP.JSON` types visible to Assets scripts. The phase-01 rationale
  ("users only compile what they reference") does not hold either: Unity compiles and ships every
  asmdef in the package regardless of `autoReferenced`; player size is governed by managed
  stripping. (a) is the only option that meets #7 AC1. RateLimit and Unity.WebSocket are added to
  the issue's list because they are user-facing modules of the same kind.
- **Bootstrap placement in Complete**, not a new `TurboHTTP.Bootstrap` asmdef: Complete already
  references every module, already excludes WebGL, references UnityEngine and is `None` in
  `check_repo.py ALLOWED_REFS`, so no module rule changes. Complete now has a purpose.
- **Always register, never "only if empty".** SubsystemRegistration is the earliest user-code hook,
  so nothing can have used a transport yet; registering unconditionally gives every play session
  (also with domain reload disabled) the same defaults. A user transport registered from
  `BeforeSceneLoad` or later, or passed through `UHttpClientOptions.Transport`, wins. A user
  registration made in `SubsystemRegistration` itself is unordered against ours (documented).
- **#9: build guard only.** No further `excludePlatforms: [WebGL]` on the modules: switching the
  editor to WebGL would then surface CS0246 errors in user code before the guard can explain. Core
  stays compiled on WebGL. There is no opt-out define; WebGL support is #130.
- **Review round 1.** `[assembly: AlwaysLinkAssembly]` on TurboHTTP.Complete: nothing references
  the assembly, so without it UnityLinker may skip it and the `[Preserve]` bootstrap never runs.
  Enter Play Mode without domain reload re-runs the bootstrap; it now detaches the default
  transport the previous session created (`HttpTransportFactory.DetachCreatedDefault()`, internal,
  Core IVT to TurboHTTP.Complete), registers the defaults, then disposes the old transport
  (architecture.md "Static state in Unity code"). An unread default is not created just to be
  disposed. WebSocket transports are per-call and caller-owned, so nothing to dispose there.
- Lanes leave `Development/docs/agents/*.md` alone; after this wave, `architecture.md` (Complete row
  "contains no scripts", Transport section on `[ModuleInitializer]`) and `platform.md:32-33` need
  the bootstrap wording.

## Tests

- `Tests/Runtime/Transport/TransportBootstrapTests.cs` (PlayMode): attribute contract
  (SubsystemRegistration, Preserve on type and method, InitializeOnLoadMethod in the Editor); after
  `Reset()` the bootstrap alone makes `HttpTransportFactory.Default`, `CreateWithBackend`,
  `new UHttpClient()` and `WebSocketTransportFactory.Create()` resolve raw-socket transports.
  TearDown re-runs the bootstrap so later fixtures see play-mode-entry state. Round 1 added the
  `AlwaysLinkAssembly` assertion, a counting fake that `Default` created and a second bootstrap
  run disposes exactly once (fails on the first head: never disposed), and a check that an unread
  previous default is never created.
- `Tests/Editor/WebGlBuildGuardTests.cs` (EditMode): WebGL throws `BuildFailedException` with the
  message; iOS, Android, Windows, macOS, Linux build normally.
- `Tests/Editor/ModuleAutoReferenceTests.cs` (EditMode): user-facing asmdefs are autoReferenced,
  opt-in ones are not.

## Deferred

- #1 AC1 (Mono standalone player without manual registration) and AC3 (CI player-build smoke
  test) need a player build: #100. Device validation (IL2CPP iOS/Android, High stripping): #99.
- #7 AC2 (samples compile when imported): #98.
- WebGL support itself: #130. The Asset Store page is not in the repo; the phase-17 draft is updated.

## Verification

- `./Tools~/ci/check.sh`: OK (invariants, format, shadow build of 19 assemblies incl. the editor
  variant with `UNITY_EDITOR`).
- Not run: Unity EditMode/PlayMode (lanes do not run Unity; host editor runs them periodically),
  any player build, IL2CPP, device. Test assemblies are not part of the shadow build.
