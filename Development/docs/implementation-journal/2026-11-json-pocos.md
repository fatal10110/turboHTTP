# 2026-11: JsonUtility serializer by default; WebSocket JSON without the reflection mapper (#271, part of #192)

## What
- **#271.** New public `TurboHTTP.JSON.JsonUtilitySerializer : IJsonSerializer` (assembly
  TurboHTTP.Complete, `Runtime/Bootstrap/JsonUtilitySerializer.cs`). Custom types go through the
  non-generic `JsonUtility.ToJson(object)` / `JsonUtility.FromJson(string, Type)`; `object`,
  `Dictionary<string, object>`, `List<object>` and scalars go through `LiteJsonSerializer`; a top-level
  JSON array is read into `T[]` element by element (LiteJson parse, `Array.CreateInstance`, each element
  re-serialized and handed to `JsonUtility`); arrays/lists/dictionaries are written element by element.
  Top-level `List<T>`/`Dictionary<string, T>` of a custom `T` and anonymous types throw
  `JsonSerializationException` (anonymous types would otherwise serialize as `{}`).
  `TurboHttpBootstrap.RegisterJsonBridge` now also calls `JsonUtilitySerializer.RegisterAsDefaultIfBuiltIn()`,
  which installs it only while `JsonSerializer.Default` is still `LiteJsonSerializer.Instance`.
  The README Quick Start (`[Serializable] MyData` + `AsJson<MyData>()`) therefore works unchanged.
- **#192.** `JsonWebSocketSerializer<T>` routes both directions through `ProjectJsonBridge` only. The
  mapper (`NormalizeForJson`, `ConvertToTargetType`, `MapToObject`, list/dictionary factories) is
  deleted: no `Activator.CreateInstance`, `MakeGenericType` or reflective `SetValue` remains, and the
  nine RS0030 baseline entries for the file are gone. XML docs describe the supported DTO shape.
- Docs: README, APIReference ("JSON serializers" section, the link target for every custom-`T`
  example), QuickStart, Troubleshooting (JsonUtility rules instead of "use properties"/`[JsonPropertyName]`),
  Core/Testing/UniTask/Unity module pages. Samples~/01-BasicUsage used anonymous types with
  `WithJsonBody` (rejected by LiteJson already, by JsonUtility too): now `[Serializable]` classes.
  Stale XML docs in `JsonSerializer`, `LiteJsonSerializer`, `JsonModuleRegistration` updated.

## Files
Runtime/Bootstrap/JsonUtilitySerializer.cs (+.meta), Runtime/Bootstrap/TurboHttpBootstrap.cs,
Runtime/WebSocket/JsonWebSocketSerializer.cs, Runtime/JSON/{JsonSerializer,JsonModuleRegistration}.cs,
Runtime/JSON/LiteJson/LiteJsonSerializer.cs, Tools~/ci/analyzer-baseline.txt,
Tests/Runtime/JSON/JsonUtilitySerializerTests.cs (+.meta), Tests/Runtime/JSON/JsonSerializerRegistryTests.cs,
Tests/Runtime/WebSocket/JsonWebSocketSerializerBridgeTests.cs (+.meta),
Tests/Runtime/WebSocket/WebSocketSerializationTests.cs, README.md, Documentation~/*, Samples~/01-BasicUsage.

## Decisions
- **Assembly: TurboHTTP.Complete.** `TurboHTTP.JSON` is `noEngineReferences`; `TurboHTTP.Unity` may not
  reference JSON; a new `TurboHTTP.Unity.Json` asmdef would be a new cross-module reference needing an
  architecture-table exception. Complete already references JSON and UnityEngine and holds the bootstrap.
  Consequence: architecture.md's Complete row ("its only code is the TurboHttpBootstrap default
  registration") is now stale; it is a hot-group file this lane may not edit (recorded in the PR).
  Projects without Complete (WebGL, or excluded) keep LiteJson and register their own serializer.
- **Registration keeps user choices.** Install only over the built-in default, so a serializer set
  earlier (editor script, previous Enter-Play-Mode session without domain reload) survives.
  `HasCustomSerializer()` now returns true after startup (documented); `ResetToDefault()` still means
  LiteJson. The check-then-set is not atomic with a concurrent `SetDefault`; both run on the main
  thread at startup, so no lock across assemblies was added.
- **WebSocket DTOs.** No fallback: what the registered serializer cannot map fails with
  `SerializationFailed`. The regression test uses a DTO with only a private parameterless
  constructor: the old mapper built it through `Activator.CreateInstance(nonPublic: true)` from the
  dictionary fallback; now the result is `SerializationFailed` with exactly one serializer call.
- **Threading.** Unity documents `JsonUtility.ToJson/FromJson` as callable from background threads;
  `Deserialize_OffTheMainThread_Works` exercises it through `Task.Run` in the Editor.

## Deferred
- #192 IL2CPP High-stripping player run (nested list + dictionary DTO): device/player validation, #99
  (owner hardware). Dictionary fields are not a JsonUtility shape: they need a full serializer via
  `JsonSerializer.SetDefault`. #192 stays open ("Part of").
- #75: POCO support for the engine-free LiteJson default (projects without TurboHTTP.Complete).

## How verified
- `./Tools~/ci/check.sh --full`: OK (invariants, format, editor/iOS/Android shadow builds, doc snippets).
- Unity 2021.3.45f2 PlayMode, filter
  `JsonSerializerRegistryTests|JsonExtensionsTests|JsonBridgeRegistrationTests|UHttpRequestBuilderJsonTests|CoroutineWrapperJsonBridgeTests|LiteJsonSerializerTests|WebSocketSerializationTests|JsonUtilitySerializerTests|JsonWebSocketSerializerBridgeTests`:
  74/74 passed. With the old `JsonWebSocketSerializer` and without the bootstrap line, the filter
  `JsonWebSocketSerializerBridgeTests|ReadmeQuickStart|StartupRegistration_` failed 4/7 (README
  `AsJson<MyData>` threw "Cannot convert 'Dictionary`2' to 'MyData'"; both no-fallback WS tests did not throw).
- Not run: full Unity suites (periodic host-editor run), EditMode (no Editor test touched), IL2CPP
  player builds, devices (`needs-device-validation`: `Array.CreateInstance` for value-type element
  arrays and JsonUtility field preservation under High stripping).
