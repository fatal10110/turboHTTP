# 2026-10 Linker processor for package link.xml (#8, #35)

## What
- UnityLinker does not read `link.xml` inside UPM packages. The new `Editor/TurboHttpLinkerProcessor.cs`
  (`IUnityLinkerProcessor`) merges every `Runtime/**/link.xml` of the package into one file
  (`Temp/TurboHTTP/TurboHTTP.link.xml`, one `<assembly>` element per assembly name) and returns
  it to UnityLinker.
- Each `link.xml` belongs to the assembly of the nearest `.asmdef` above it. It is merged only when
  that assembly's DLL is in `UnityLinkerBuildPipelineData.inputDirectory`, so module rules apply only
  when the module ships.
- The whole-assembly BouncyCastle preserve moved from `Runtime/Transport/link.xml` to
  `Runtime/Transport/BouncyCastle/link.xml` (owned by `TurboHTTP.Transport.BouncyCastle`).
- New preserve rules for the reflection targets #8 lists:
  - `TurboHTTP.JSON.JsonSerializer` (ProjectJsonBridge, RecordReplayTransport) in `Runtime/JSON/link.xml`;
  - `TurboHTTP.Observability.MonitorInterceptor` in `Runtime/Observability/link.xml` (the reflection
    path in `UHttpClient` is `#if UNITY_EDITOR` today, so this is defensive);
  - `TransportModuleInitializer`, `RawSocketTransport` in `Runtime/Transport/link.xml`;
  - `WebSocketTransportModuleInitializer`, `RawSocketWebSocketTransport` in the new
    `Runtime/WebSocket.Transport/link.xml`.
- #35: SslStream, SslClientAuthenticationOptions and SslApplicationProtocol are now preserved under
  assembly `System` (where Unity's class libraries implement them); the `System.Net.Security` entry is
  kept for other profiles.
- Closed generic names (`ValueTask`1[[...]]`, `SingleReaderChannel`1[[...]]`) removed from
  `Runtime/Transport/link.xml` and `Runtime/Core/link.xml`; UnityLinker ignores them and the open
  templates were already listed.
- `Documentation~/PlatformNotes.md`: link.xml section now describes the processor and uses the
  `System` assembly in the sample.

## Files
- `Editor/TurboHttpLinkerProcessor.cs`, `Editor/AssemblyInfo.cs` (InternalsVisibleTo TurboHTTP.Tests.Editor)
- `Runtime/{Core,JSON,Observability,Transport}/link.xml`, new `Runtime/Transport/BouncyCastle/link.xml`,
  new `Runtime/WebSocket.Transport/link.xml` (+ `.meta`)
- `Tests/Editor/TurboHttpLinkerProcessorTests.cs`
- `Documentation~/PlatformNotes.md`

## Decisions
- Package root: `PackageInfo.FindForAssembly(...).resolvedPath`, falling back to the
  `TurboHTTP.Editor` asmdef location (copy embedded under `Assets/`).
- When the package root cannot be found the processor throws `BuildFailedException` instead of
  silently producing a player whose reflection targets are stripped.
- When `inputDirectory` is missing every file is merged (fail toward preserving).
- The #35 test asserts, on the running class library, that the merged file has an entry under the
  assembly `typeof(SslStream|SslClientAuthenticationOptions|SslApplicationProtocol).Assembly` really
  is, and that framework assembly names come from an explicit allow-list
  (`mscorlib`, `System`, `System.Net.Security`, `System.Security.Cryptography.Algorithms`).
- Rules stay per module (no central list in the processor) so other lanes edit only their module's
  `link.xml`. If #67/#95 (json-registration) or #1 (bootstrap) remove a reflection path, drop the
  matching rule and the matching `TestCase` in `TurboHttpLinkerProcessorTests`.

## Deferred
- IL2CPP High build without BC contains no `TurboHTTP.SecureProtocol` types: CI player build (#100).
  Actually excluding BC from builds that do not use it is #143.
- IL2CPP device smoke test, ALPN members kept and HTTP/2 negotiated over SslStream: #99
  (needs-device-validation).
- `JsonWebSocketSerializer` coverage in the WebSocket link.xml (review row M) is outside #8's list.

## Verification
- `'./Tools~/ci/check.sh'`: pass (repo invariants, format, shadow build incl. `TurboHTTP.Editor`).
- The new test file was compiled against the shadow-built `TurboHTTP.Editor` and NUnit 3.14 outside
  Unity (build succeeded, no warnings).
- Not run: Unity EditMode/PlayMode (Unity cannot be driven from this host; PR CI runs them), IL2CPP
  player builds, devices.
