# 2026-11 Player-build smoke (#1, #8, #7; records for #196, #258, #89, #221)

## What

A host script that builds real players from a throwaway project consuming a synced copy of the
package, inspects which TurboHTTP assemblies survived into the build, and runs the Windows players.
The registration, linker processor and auto-reference code for #1, #8 and #7 was already on `main`
(#216, `2026-10-bootstrap.md`, `2026-10-linker-processor.md`); what was missing was a player build
that exercises it. No product code changed.

## Files

- `Tools~/unity/build-player.sh`: `--target win64-mono|win64-il2cpp|android-il2cpp [--bouncycastle] [--background]`.
  It reuses the `run-tests.sh` conventions (WSL `Unity.exe` + `wslpath -m`, a cache under
  `%LOCALAPPDATA%\turbohttp`, rsync of the package, `-createProject`, `manifest.json` edit; exit 3 =
  Unity, a build module or the native toolchain missing). Env `UNITY_SMOKE_DIR` holds `testproj/`,
  `package/` and `builds/<variant>-<timestamp>/` (build log, player log, the `TurboHTTP.link.xml`
  UnityLinker received, the player).
- `Tools~/unity/player-smoke/TurboHttpPlayerSmoke.cs`: copied to `Assets/TurboHttpSmoke/` with no
  `.asmdef` (so it compiles into Assembly-CSharp). A `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`
  builds a `UHttpClient` with `RetryInterceptor` + `AuthInterceptor`, reads
  `HttpTransportFactory.Default`, creates a WebSocket transport through `WebSocketTransportFactory.Create()`
  and a `WebSocketClient`, round-trips JSON through `JsonSerializer`, and prints
  `TURBOHTTP_SMOKE PASS http=... ws=... json=ok bc=present|absent`. It never calls any
  `EnsureRegistered()` and never touches the network.
- `Tools~/unity/player-smoke/QuickStartExample.cs`: the README "Quick Start" block copied verbatim
  (JSON, `SendBufferedAsync`, `UHttpException`), compiled into the same Assembly-CSharp; the
  component is never put in the scene, so it sends nothing.
- `Tools~/unity/player-smoke/Editor/TurboHttpPlayerBuild.cs`: `-executeMethod TurboHttpPlayerBuild.Build`.
  Sets the backend (IL2CPP: Managed Stripping Level High; Mono: Disabled, Unity's default), re-checks
  the `TURBOHTTP_BOUNCYCASTLE` define, builds one empty scene and copies
  `Temp/TurboHTTP/TurboHTTP.link.xml` next to the build.

## Decisions

- Defines are written into `ProjectSettings.asset` for the target's group before Unity starts (as
  `run-tests.sh` does), so the Editor and the player compile see the same set; the variant without
  a flag removes the define.
- IL2CPP linker output is read from `Library/Bee/artifacts/<program>/ManagedStripped/`, which the
  script deletes before each build so a previous variant's output cannot satisfy the checks.
- Mono copies every compiled assembly (stripping Disabled), so a Mono build without the define
  reports `TurboHTTP.SecureProtocol.dll` instead of failing on it; the "absent" assertion is made
  on IL2CPP High only, which is what #8 AC2 and #196 AC1 are about.
- The built-in JSON serializer rejects POCOs ("Use a dictionary or register a custom serializer"),
  so the smoke round-trips a `Dictionary<string, object>`.
- #7 AC1 is proven by compiling the README Quick Start verbatim (`QuickStartExample.cs`, after #269
  rewrote it) plus the Retry/Auth/WebSocket usage in `TurboHttpPlayerSmoke.cs` from an `Assets/`
  folder without an asmdef, in every player build below. #7 AC2 (samples compile when imported)
  was delivered by #269: each sample has an asmdef compiled by the shadow build, and the
  docs-customer lane compiled the five samples in a clean 2021.3 project (`2026-11-docs-customer.md`).
  Running the samples stays in #268.
- The Quick Start's `response.AsJson<MyData>()` compiles but throws at runtime with the bundled
  serializer (custom types are rejected): #271.
- WSL interop intermittently fails to start Windows processes (`UtilAcceptVsock: accept4 failed`);
  the script retries a Unity or player start that wrote no log and skips the `cmd.exe` probe when
  `UNITY_SMOKE_DIR` is set. A `find` over `Library/Bee` on `/mnt/c` once failed with "Cannot
  allocate memory" and hid the linker output, so the script uses the fixed
  `Library/Bee/artifacts/{Android,WinPlayerBuildProgram}/ManagedStripped` paths instead.

## Results (host: WSL2 + Windows Unity 2021.3.45f2, package at the PR head; all rows rerun 2026-11-02 after merging #269 and adding `QuickStartExample.cs`)

| Build | Result |
| --- | --- |
| win64-mono, no BC | build OK; player `TURBOHTTP_SMOKE PASS http=TurboHTTP.Transport.RawSocketTransport ws=TurboHTTP.WebSocket.Transport.RawSocketWebSocketTransport json=ok bc=absent`; no `TurboHTTP.Transport.BouncyCastle.dll`; `TurboHTTP.SecureProtocol.dll` copied (Mono, see Decisions) |
| win64-mono, `--bouncycastle` | build OK; player PASS with `bc=present`; both BC assemblies in `Managed/` |
| win64-il2cpp (both variants) | not run: `TOOLCHAIN_UNAVAILABLE`, "Windows 10 SDK is not installed" (IL2CPP toolchain setup fails before UnityLinker runs) |
| android-il2cpp, High, no BC | APK built with no Gradle edits; `ManagedStripped/` holds Auth, Complete, Core, JSON, Retry, Transport, Unity, WebSocket, WebSocket.Transport and neither `TurboHTTP.Transport.BouncyCastle` nor `TurboHTTP.SecureProtocol`; the processor's link.xml names no BC assembly |
| android-il2cpp, High, `--bouncycastle` | APK built; both BC assemblies in `ManagedStripped/`; the link.xml carries the `TurboHTTP.Transport.BouncyCastle` rule |
| android-il2cpp, High, `--background` | APK built with no Gradle edits; `classes.dex` holds `com.turbohttp.background.TurboHttpBackgroundWorker` and `androidx.work`; the merged manifest carries the WorkManager entries |

`TurboHTTP.SecureProtocol` is therefore stripped under High without the define, so the vendored
`Lib/TurboHTTP.SecureProtocol.asmdef` needs no define constraint (#258's open question).

## Not run

- Unity EditMode/PlayMode suites: nothing under `Tests/` changed (host editor, periodic).
- IL2CPP Windows player (no Windows 10 SDK on this host) and therefore no IL2CPP player run; the
  Android APKs were built, not installed or launched.
- Devices (iOS, Android): #99. CI player builds (license): #100. `build-player.sh` is the script
  a CI job would call.
